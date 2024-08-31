using Alethic.Kademlia;
using Hangfire;
using PgpCore;
using Subverse.Abstractions;
using Subverse.Exceptions;
using Subverse.Implementations;
using Subverse.Models;
using Subverse.Stun;
using System.Collections.Concurrent;
using System.Net;
using static Subverse.Models.SubverseMessage;

namespace Subverse.Server
{
    internal class RoutedHubService : IPeerService
    {
        private const string DEFAULT_CONFIG_HOSTNAME = "default.subverse";
        private const int DEFAULT_CONFIG_START_TTL = 99;

        private readonly IConfiguration _configuration;
        private readonly ILogger<RoutedHubService> _logger;
        private readonly IMessageQueue<string> _messageQueue;
        private readonly IPgpKeyProvider _keyProvider;
        private readonly IStunUriProvider _stunUriProvider;

        private readonly string _configHostname;
        private readonly int _configStartTTL;

        private readonly ConcurrentDictionary<KNodeId160, Task> _taskMap;
        private readonly ConcurrentDictionary<KNodeId160, CancellationTokenSource> _ctsMap;
        private readonly ConcurrentDictionary<KNodeId160, HashSet<IPeerConnection>> _connectionMap;

        private readonly ConcurrentDictionary<KNodeId160, TaskCompletionSource<EncryptionKeys>> _entityKeysSources;
        private readonly EncryptionKeys _myEntityKeys;

        private IPEndPoint? _localEndPoint;
        private SubversePeer? _cachedSelf;

        public KNodeId160 ConnectionId { get; }

        public RoutedHubService(
            IConfiguration configuration,
            ILogger<RoutedHubService> logger,
            IMessageQueue<string> messageQueue,
            IPgpKeyProvider keyProvider,
            IStunUriProvider stunUriProvider)
        {
            _configuration = configuration;

            _configHostname = _configuration.GetSection("HubService")?
                .GetValue<string?>("Hostname") ?? DEFAULT_CONFIG_HOSTNAME;

            _configStartTTL = _configuration.GetSection("HubService")?
                .GetValue<int?>("StartTTL") ?? DEFAULT_CONFIG_START_TTL;

            QuicPeerConnection.DEFAULT_CONFIG_START_TTL = _configStartTTL;

            _logger = logger;
            _messageQueue = messageQueue;
            _keyProvider = keyProvider;
            _stunUriProvider = stunUriProvider;

            _myEntityKeys = new EncryptionKeys(
                _keyProvider.GetPublicKeyFile(),
                _keyProvider.GetPrivateKeyFile(),
                _keyProvider.GetPrivateKeyPassPhrase()
                );
            _entityKeysSources = new();
            ConnectionId = new(_myEntityKeys.PublicKey.GetFingerprint());

            _taskMap = new ConcurrentDictionary<KNodeId160, Task>();
            _ctsMap = new ConcurrentDictionary<KNodeId160, CancellationTokenSource>();
            _connectionMap = new ConcurrentDictionary<KNodeId160, HashSet<IPeerConnection>>();

            // Schedule queue flushing job
            RecurringJob.AddOrUpdate(
                "Subverse.Server.RoutedHubService.FlushMessagesAsync",
                () => FlushMessagesAsync(CancellationToken.None),
                Cron.Minutely);
        }

        public async Task<KNodeId160> OpenConnectionAsync(IPeerConnection peerConnection, SubverseMessage? message, CancellationToken cancellationToken)
        {
            KNodeId160 connectionId = await peerConnection
                .CompleteHandshakeAsync(message, cancellationToken);
            if (message is null)
            {
                // Setup connection for routing & message events
                peerConnection.MessageReceived += Connection_MessageReceived;
            }

            HashSet<IPeerConnection> newConnections = [peerConnection];
            _connectionMap.AddOrUpdate(connectionId, newConnections,
                (key, existingConnections) =>
                {
                    lock (existingConnections)
                    {
                        existingConnections.UnionWith(newConnections);
                        return existingConnections;
                    }
                });


            // Immediately send all messages we've cached for this particular entity (in the background)
            var newCts = new CancellationTokenSource();
            _ctsMap.AddOrUpdate(connectionId, newCts,
                (key, oldCts) =>
                {
                    oldCts.Dispose();
                    return newCts;
                });

            Func<KNodeId160, Task> newTaskFactory = (key) =>
                Task.Run(() => FlushMessagesAsync(key, newCts.Token));

            _ = _taskMap.AddOrUpdate(connectionId, newTaskFactory, (key, oldTask) =>
                {
                    try
                    {
                        oldTask.Wait();
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex.Message);
                    }

                    return newTaskFactory(key);
                });

            return connectionId;
        }

        public async Task CloseConnectionAsync(IPeerConnection connection, KNodeId160 connectionId, CancellationToken cancellationToken)
        {
            _ctsMap.Remove(connectionId, out CancellationTokenSource? storedCts);
            storedCts?.Dispose();

            _taskMap.Remove(connectionId, out Task? storedTask);
            try
            {
                if (storedTask is not null) await storedTask;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, null);
            }

            if (_connectionMap.TryRemove(connectionId, out HashSet<IPeerConnection>? storedConnections))
            {
                storedConnections.Remove(connection);
                if (storedConnections.Any())
                {
                    _connectionMap.AddOrUpdate(connectionId, storedConnections,
                        (key, existingConnections) =>
                        {
                            lock (existingConnections)
                            {
                                existingConnections.UnionWith(storedConnections);
                                return existingConnections;
                            }
                        });
                }
            }


            HashSet<IPeerConnection> allConnections =
                _connectionMap.Values
                .SelectMany(x => x)
                .ToHashSet();

            if (allConnections.Contains(connection))
            {
                connection.Dispose();
            }
        }

        public async Task FlushMessagesAsync(KNodeId160 connectionId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = await _messageQueue.DequeueByKeyAsync(connectionId.ToString());

            while (message is not null)
            {
                await RouteMessageAsync(message);

                cancellationToken.ThrowIfCancellationRequested();
                message = await _messageQueue.DequeueByKeyAsync(connectionId.ToString());
            }
        }

        public async Task FlushMessagesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var keyedMessage = await _messageQueue.DequeueAsync();

            while (keyedMessage is not null)
            {
                await RouteMessageAsync(keyedMessage.Message);

                cancellationToken.ThrowIfCancellationRequested();
                keyedMessage = await _messageQueue.DequeueAsync();
            }
        }

        public SubversePeer GetSelf()
        {
            lock (this)
            {
                if (_cachedSelf is null)
                {
                    IPEndPoint serviceEndPoint = GetRemoteEndPointAsync(30603).Result;

                    return _cachedSelf = new SubversePeer(
                            _configHostname,
                            new UriBuilder()
                            {
                                Scheme = "subverse",
                                Host = serviceEndPoint.Address.ToString(),
                                Port = serviceEndPoint.Port
                            }.ToString(),
                            DateTime.UtcNow
                            );
                }
                else
                {
                    return _cachedSelf;
                }
            }
        }

        public void SetLocalEndPoint(IPEndPoint localEndPoint)
        {
            _localEndPoint = localEndPoint;
        }

        public async Task<IPEndPoint> GetRemoteEndPointAsync(int? localPortNum = null)
        {
            Exception? exInner = null;
            string exMessage = "GetSelf: NAT traversal via STUN failed to obtain an external address for local port: " +
                (localPortNum?.ToString() ?? _localEndPoint?.Port.ToString() ?? "<unspecified>");
            try
            {
                var stunClient = new StunClientUdp();

                StunMessage? stunResponse = null;
                await foreach (var uri in _stunUriProvider.GetAvailableAsync().Take(8))
                {
                    stunResponse = await stunClient.SendRequestAsync(new StunMessage([]),
                            localPortNum ?? _localEndPoint?.Port ?? 0, uri ?? string.Empty);
                    break;
                }

                foreach (var stunAttr in stunResponse?.Attributes ?? [])
                {
                    switch (stunAttr.Type)
                    {
                        case StunAttributeType.MAPPED_ADDRESS:
                            return stunAttr.GetMappedAddress();
                        case StunAttributeType.XOR_MAPPED_ADDRESS:
                            return stunAttr.GetXorMappedAddress();
                    }
                }
            }
            catch (Exception ex) { exInner = ex; }

            throw new InvalidEntityException(exMessage, exInner);
        }

        private async void Connection_MessageReceived(object? sender, MessageReceivedEventArgs e)
        {
            if (!e.Message.Recipient.Equals(ConnectionId))
            {
                await RouteMessageAsync(e.Message);
            }
            else
            {
                await ProcessMessageAsync(e.Message);
            }
        }

        private async Task ProcessMessageAsync(SubverseMessage message)
        {
            switch (message.Code)
            {
                case ProtocolCode.Entity:
                    await ProcessEntityAsync(message);
                    break;
            }
        }

        private async Task ProcessEntityAsync(SubverseMessage message)
        {
            CertificateCookie theirCookie;
            TaskCompletionSource<EncryptionKeys>? entityKeysSource;

            theirCookie = (CertificateCookie)CertificateCookie.FromBlobBytes(message.Content);
            if (!_entityKeysSources.TryGetValue(theirCookie.Key, out entityKeysSource))
            {
                entityKeysSource = new TaskCompletionSource<EncryptionKeys>();
                _entityKeysSources.TryAdd(theirCookie.Key, entityKeysSource);
            }

            if (!entityKeysSource.TrySetResult(theirCookie.KeyContainer)) { return; }

            LocalCertificateCookie myCookie = new LocalCertificateCookie(
                _keyProvider.GetPublicKeyFile().OpenRead(),
                _myEntityKeys, GetSelf() with { DhtUri = null });

            await RouteMessageAsync(
                new SubverseMessage(
                    theirCookie.Key, DEFAULT_CONFIG_START_TTL,
                    ProtocolCode.Entity, myCookie.ToBlobBytes()
                ));
        }

        private async Task RouteMessageAsync(SubverseMessage message)
        {
            if (message.TimeToLive < 0)
            {
                await RouteMessageAsync(message with { TimeToLive = _configStartTTL });
            }
            else if (
                message.TimeToLive > 0 && 
                _connectionMap.TryGetValue(message.Recipient,
                    out HashSet<IPeerConnection>? connections) && 
                connections.Any())
            {
                // Forward the message to everyone interested in talking to the recipient.
                var nextHopMessage = message with { TimeToLive = message.TimeToLive - 1 };

                using var cts = new CancellationTokenSource();
                var allTasks = connections.Select(x => 
                    Task.Run(() => x.SendMessage(nextHopMessage), cts.Token)
                    ).ToHashSet();

                while (allTasks.Any())
                {
                    var completedTask = await Task.WhenAny(allTasks);
                    try
                    {
                        await completedTask;
                        cts.Cancel();
                    }
                    catch (Exception) 
                    {
                        allTasks.Remove(completedTask);
                    }
                }
            }
            // Otherwise, if this message has a valid TTL value...
            else if (message.TimeToLive > 0)
            {
                // Our only hopes of contacting this peer have run out!! For now...
                // Queue this message for future delivery.
                await _messageQueue.EnqueueAsync(message.Recipient.ToString(), message);
            }
        }
    }
}
