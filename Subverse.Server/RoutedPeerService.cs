using Hangfire;
using PgpCore;
using SIPSorcery.SIP;
using Subverse.Abstractions;
using Subverse.Implementations;
using Subverse.Models;
using Subverse.Types;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
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
        private readonly IPgpKeyProvider _keyProvider;

        private readonly string _configHostname;
        private readonly int _configStartTTL;

        private readonly ConcurrentDictionary<SubversePeerId, HashSet<IPeerConnection>> _connectionMap;
        private readonly ConcurrentDictionary<string, string> _callerMap;

        private readonly ConcurrentDictionary<SubversePeerId, TaskCompletionSource<EncryptionKeys>> _entityKeysSources;
        private readonly EncryptionKeys _myEntityKeys;

        private readonly SIPUDPChannel _sipChannel;
        private readonly SIPTransport _sipTransport;

        public IPEndPoint? LocalEndPoint { get; set; }
        public IPEndPoint? RemoteEndPoint { get; set; }

        public SubversePeerId ConnectionId { get; }

        public RoutedPeerService(
            IConfiguration configuration,
            ILogger<RoutedPeerService> logger,
            IPgpKeyProvider keyProvider)
        {
            _configuration = configuration;

            _configHostname = _configuration.GetSection("HubService")?
                .GetValue<string?>("Hostname") ?? DEFAULT_CONFIG_HOSTNAME;

            _configStartTTL = _configuration.GetSection("HubService")?
                .GetValue<int?>("StartTTL") ?? DEFAULT_CONFIG_START_TTL;

            QuicPeerConnection.DEFAULT_CONFIG_START_TTL = _configStartTTL;

            _logger = logger;
            _keyProvider = keyProvider;

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

            _connectionMap = new ConcurrentDictionary<SubversePeerId, HashSet<IPeerConnection>>();
            _callerMap = new ConcurrentDictionary<string, string>();
        }

        public async Task<SubversePeerId> OpenConnectionAsync(IPeerConnection peerConnection, SubverseMessage? message, CancellationToken cancellationToken)
        {
            SubversePeerId connectionId = await peerConnection
                .CompleteHandshakeAsync(message, cancellationToken);

            _logger.LogInformation($"Proxy of {connectionId} connected.");

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

            return connectionId;
        }

        public Task CloseConnectionAsync(IPeerConnection connection, SubversePeerId connectionId, CancellationToken cancellationToken)
        {
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

            return Task.CompletedTask;
        }

        public SubversePeer GetSelf()
        {
            return new SubversePeer(
                    _configHostname,
                    LocalEndPoint is null || RemoteEndPoint is null ?
                    null : new UriBuilder()
                    {
                        Scheme = "subverse",
                        Host = RemoteEndPoint?.Address.ToString(),
                        Port = RemoteEndPoint?.Port ?? 6_03_03,
                    }.ToString(),
                    DateTime.UtcNow
                    );
        }

        private async void Connection_MessageReceived(object? sender, MessageReceivedEventArgs e)
        {
            var connection = sender as IPeerConnection;
            if (!e.Message.Recipient.Equals(ConnectionId))
            {
                await RouteMessageAsync(e.Message);
            }
            else
            {
                await ProcessMessageAsync(connection, e.Message);
            }
        }

        private async Task ProcessMessageAsync(IPeerConnection? connection, SubverseMessage message)
        {
            switch (message.Code)
            {
                case ProtocolCode.Entity:
                    await ProcessEntityAsync(connection, message);
                    break;
                case ProtocolCode.Application:
                    await ProcessSipMessageAsync(connection, message);
                    break;
            }
        }

        private async Task RouteEntityAsync(SubversePeerId peerId)
        {
            LocalCertificateCookie myCookie = new LocalCertificateCookie(
                _keyProvider.GetPublicKeyFile().OpenRead(),
                _myEntityKeys, GetSelf() with { ServiceUri = null });

            await RouteMessageAsync(
                new SubverseMessage(
                    peerId, DEFAULT_CONFIG_START_TTL,
                    ProtocolCode.Entity, myCookie.ToBlobBytes()
                ));
        }

        private async Task<EncryptionKeys> GetEntityKeysAsync(SubversePeerId peerId)
        {
            TaskCompletionSource<EncryptionKeys>? entityKeysSource;

            if (!_entityKeysSources.TryGetValue(peerId, out entityKeysSource))
            {
                entityKeysSource = _entityKeysSources.GetOrAdd(peerId,
                    new TaskCompletionSource<EncryptionKeys>());
            }

            await RouteEntityAsync(peerId);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5.0));
            return await entityKeysSource.Task.WaitAsync(cts.Token);
        }

        private async Task ProcessEntityAsync(IPeerConnection? connection, SubverseMessage message)
        {
            CertificateCookie theirCookie;
            TaskCompletionSource<EncryptionKeys>? entityKeysSource;

            theirCookie = (CertificateCookie)CertificateCookie.FromBlobBytes(message.Content);
            if (!_entityKeysSources.TryGetValue(theirCookie.Key, out entityKeysSource))
            {
                entityKeysSource = new TaskCompletionSource<EncryptionKeys>();
                _entityKeysSources.TryAdd(theirCookie.Key, entityKeysSource);
            }

            if (entityKeysSource.TrySetResult(theirCookie.KeyContainer))
            {
                if (connection is not null)
                {
                    try
                    {
                        await OpenConnectionAsync(connection,
                            new SubverseMessage(theirCookie.Key,
                                0, ProtocolCode.Command, []),
                            default);
                    }
                    catch (QuicException) { }
                }

                await RouteEntityAsync(theirCookie.Key);
            }
        }

        private async Task ProcessSipMessageAsync(IPeerConnection? connection, SubverseMessage message)
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

            EncryptionKeys entityKeys;
            try
            {
                entityKeys = await GetEntityKeysAsync(toEntityId);
            }
            catch (OperationCanceledException) 
            {
                return;
            }

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
            EncryptionKeys entityKeys;
            try
            {
                entityKeys = await GetEntityKeysAsync(fromEntityId);
            }
            catch (OperationCanceledException)
            {
                return;
            }

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
        }
    }
}
