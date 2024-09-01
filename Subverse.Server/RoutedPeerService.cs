using Hangfire;
using PgpCore;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using Subverse.Abstractions;
using Subverse.Exceptions;
using Subverse.Implementations;
using Subverse.Models;
using Subverse.Stun;
using Subverse.Types;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using static Subverse.Models.SubverseMessage;

namespace Subverse.Server
{
    internal class RoutedPeerService : IPeerService
    {
        private const string DEFAULT_CONFIG_HOSTNAME = "default.subverse";
        private const int DEFAULT_CONFIG_START_TTL = 99;

        private readonly IConfiguration _configuration;
        private readonly ILogger<RoutedPeerService> _logger;
        private readonly IMessageQueue<string> _messageQueue;
        private readonly IPgpKeyProvider _keyProvider;
        private readonly IStunUriProvider _stunUriProvider;

        private readonly string _configHostname;
        private readonly int _configStartTTL;

        private readonly ConcurrentDictionary<SubversePeerId, Task> _taskMap;
        private readonly ConcurrentDictionary<SubversePeerId, CancellationTokenSource> _ctsMap;
        private readonly ConcurrentDictionary<SubversePeerId, HashSet<IPeerConnection>> _connectionMap;
        private readonly ConcurrentDictionary<string, string> _callerMap;

        private readonly ConcurrentDictionary<SubversePeerId, TaskCompletionSource<EncryptionKeys>> _entityKeysSources;
        private readonly EncryptionKeys _myEntityKeys;

        private readonly SIPUDPChannel _sipChannel;
        private readonly SIPTransport _sipTransport;

        private IPEndPoint? _localEndPoint;
        private SubversePeer? _cachedSelf;

        public SubversePeerId ConnectionId { get; }

        public RoutedPeerService(
            IConfiguration configuration,
            ILogger<RoutedPeerService> logger,
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

            if (!_keyProvider.GetPublicKeyFile().Exists || !_keyProvider.GetPrivateKeyFile().Exists)
            {
                using var pgp = new PGP();
                pgp.GenerateKey(
                    publicKeyFileInfo: _keyProvider.GetPublicKeyFile(),
                    privateKeyFileInfo: _keyProvider.GetPrivateKeyFile(),
                    username: GetSelf().Hostname,
                    password: _keyProvider.GetPrivateKeyPassPhrase()
                    );
            }

            _myEntityKeys = new EncryptionKeys(
                _keyProvider.GetPublicKeyFile(),
                _keyProvider.GetPrivateKeyFile(),
                _keyProvider.GetPrivateKeyPassPhrase()
                );
            _entityKeysSources = new();
            ConnectionId = new(_myEntityKeys.PublicKey.GetFingerprint());

            _logger.LogInformation(ConnectionId.ToString());

            _sipChannel = new SIPUDPChannel(IPAddress.Loopback, 5060);
            _sipTransport = new SIPTransport(true, Encoding.UTF8, Encoding.UTF8);
            _sipTransport.AddSIPChannel(_sipChannel);

            _sipTransport.SIPTransportRequestReceived += SipRequestReceived;
            _sipTransport.SIPTransportResponseReceived += SipResponseReceived;

            _taskMap = new ConcurrentDictionary<SubversePeerId, Task>();
            _ctsMap = new ConcurrentDictionary<SubversePeerId, CancellationTokenSource>();
            _connectionMap = new ConcurrentDictionary<SubversePeerId, HashSet<IPeerConnection>>();
            _callerMap = new ConcurrentDictionary<string, string>();

            // Schedule queue flushing job
            RecurringJob.AddOrUpdate(
                "Subverse.Server.RoutedHubService.FlushMessagesAsync",
                () => FlushMessagesAsync(CancellationToken.None),
                Cron.Minutely);
        }

        public async Task<SubversePeerId> OpenConnectionAsync(IPeerConnection peerConnection, SubverseMessage? message, CancellationToken cancellationToken)
        {
            SubversePeerId connectionId = await peerConnection
                .CompleteHandshakeAsync(message, cancellationToken);

            // Setup connection for routing & message events
            peerConnection.MessageReceived += Connection_MessageReceived;

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

            Func<SubversePeerId, Task> newTaskFactory = (key) =>
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

        public async Task CloseConnectionAsync(IPeerConnection connection, SubversePeerId connectionId, CancellationToken cancellationToken)
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

        public async Task FlushMessagesAsync(SubversePeerId connectionId, CancellationToken cancellationToken)
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
                IPAddress? serviceAddr = Dns
                    .GetHostAddresses(Environment.MachineName)
                    .FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                return _cachedSelf = new SubversePeer(
                        _configHostname,
                        new UriBuilder()
                        {
                            Scheme = "subverse",
                            Host = serviceAddr?.ToString() ?? _configHostname,
                            Port = _localEndPoint?.Port ?? 30603
                        }.ToString(),
                        DateTime.UtcNow
                        );
            }
        }

        public void SetLocalEndPoint(IPEndPoint localEndPoint)
        {
            _localEndPoint = localEndPoint;
        }

        private async Task<IPEndPoint> GetRemoteEndPointAsync(int? localPortNum = null)
        {
            Exception? exInner = null;
            string exMessage = "GetSelf: NAT traversal via STUN failed to obtain an external address for local port: " +
                (localPortNum?.ToString() ?? _localEndPoint?.Port.ToString() ?? "<unspecified>");
            try
            {
                var stunClient = new StunClientUdp();

                StunMessage? stunResponse = null;
                await foreach (var uri in _stunUriProvider.GetAvailableAsync())
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
                case ProtocolCode.Application:
                    await ProcessSipMessageAsync(message);
                    break;
            }
        }

        private async Task RouteEntityAsync(SubversePeerId peerId)
        {
            LocalCertificateCookie myCookie = new LocalCertificateCookie(
                _keyProvider.GetPublicKeyFile().OpenRead(),
                _myEntityKeys, GetSelf() with { DhtUri = null });

            await RouteMessageAsync(
                new SubverseMessage(
                    peerId, DEFAULT_CONFIG_START_TTL,
                    ProtocolCode.Entity, myCookie.ToBlobBytes()
                ));
        }

        async Task<EncryptionKeys> GetEntityKeysAsync(SubversePeerId peerId)
        {
            TaskCompletionSource<EncryptionKeys>? entityKeysSource;

            if (!_entityKeysSources.TryGetValue(peerId, out entityKeysSource))
            {
                await RouteEntityAsync(peerId);

                entityKeysSource = _entityKeysSources.GetOrAdd(peerId,
                    new TaskCompletionSource<EncryptionKeys>());
            }

            return await entityKeysSource.Task;
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

            entityKeysSource.TrySetResult(theirCookie.KeyContainer);

            await RouteEntityAsync(theirCookie.Key);
        }

        private async Task ProcessSipMessageAsync(SubverseMessage message)
        {
            byte[] messageBytes;
            using (var pgp = new PGP(_myEntityKeys))
            using (var bufferStream = new MemoryStream(message.Content))
            using (var decryptStream = new MemoryStream())
            {
                await pgp.DecryptAsync(bufferStream, decryptStream);
                messageBytes = decryptStream.ToArray();
            }

            SIPRequest? request = null;
            try
            {
                request = SIPRequest.ParseSIPRequest(Encoding.UTF8.GetString(messageBytes));
                request.Header.From.FromURI.Host = "subverse";

                messageBytes = request.GetBytes();

                string fromEntityStr = request.Header.From.FromURI.User;
                _callerMap.TryAdd(request.Header.CallId, fromEntityStr);
            }
            catch (SIPValidationException) { }

            await _sipTransport.SendRawAsync(
                _sipChannel.ListeningSIPEndPoint,
                new SIPEndPoint(SIPProtocolsEnum.udp, IPAddress.Loopback, 5067),
                messageBytes
                );
        }

        private async Task SipRequestReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPRequest sipRequest)
        {
            string toEntityStr = sipRequest.Header.To.ToURI.User;
            SubversePeerId toEntityId = SubversePeerId.FromString(toEntityStr);

            EncryptionKeys entityKeys = await GetEntityKeysAsync(toEntityId);
            using (var pgp = new PGP(entityKeys))
            using (var bufferStream = new MemoryStream(sipRequest.GetBytes()))
            using (var encryptStream = new MemoryStream())
            {
                await pgp.EncryptAsync(bufferStream, encryptStream);

                await RouteMessageAsync(
                    new SubverseMessage(toEntityId,
                        DEFAULT_CONFIG_START_TTL, ProtocolCode.Application,
                        encryptStream.ToArray()
                        ));
            }
        }

        private async Task SipResponseReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPResponse sipResponse)
        {
            if (!_callerMap.TryGetValue(
                sipResponse.Header.CallId,
                out string? fromEntityStr))
            {
                return;
            }

            SubversePeerId fromEntityId = SubversePeerId.FromString(fromEntityStr);
            EncryptionKeys entityKeys = await GetEntityKeysAsync(fromEntityId);

            using (var pgp = new PGP(entityKeys))
            using (var bufferStream = new MemoryStream(sipResponse.GetBytes()))
            using (var encryptStream = new MemoryStream())
            {
                await pgp.EncryptAsync(bufferStream, encryptStream);

                await RouteMessageAsync(
                    new SubverseMessage(fromEntityId,
                        DEFAULT_CONFIG_START_TTL, ProtocolCode.Application,
                        encryptStream.ToArray()
                        ));
            }
        }

        private async Task RouteMessageAsync(SubverseMessage message)
        {
            if (message.TimeToLive < 0)
            {
                await RouteMessageAsync(message with { TimeToLive = _configStartTTL });
            }
            else if (
                message.TimeToLive >= 0 &&
                _connectionMap.TryGetValue(message.Recipient,
                    out HashSet<IPeerConnection>? connections))
            {
                SubverseMessage nextHopMessage = message with { TimeToLive = message.TimeToLive - 1 };
                HashSet<Task> allTasks;
                lock (connections)
                {
                    allTasks = connections.Select(x =>
                        Task.Run(() => x.SendMessage(nextHopMessage))
                        ).ToHashSet();
                }
            }
            // Otherwise, if this message has a valid TTL value...
            else if (message.TimeToLive >= 0)
            {
                // Our only hopes of contacting this peer have run out!! For now...
                // Queue this message for future delivery.
                await _messageQueue.EnqueueAsync(message.Recipient.ToString(), message);
            }
        }
    }
}
