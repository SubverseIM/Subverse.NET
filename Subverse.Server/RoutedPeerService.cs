using MonoTorrent;
using MonoTorrent.Connections.Dht;
using MonoTorrent.Dht;
using PgpCore;
using SIPSorcery.SIP;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Text;
using System.Threading;

namespace Subverse.Server
{
    internal class RoutedPeerService : IPeerService, IDisposable
    {
        private static readonly TimeSpan DEFAULT_BOOTSTRAP_PERIOD = TimeSpan.FromSeconds(5.0);

        private readonly ILogger<RoutedPeerService> _logger;
        private readonly IPgpKeyProvider _keyProvider;

        private readonly ConcurrentDictionary<string, SubversePeerId> _callerMap;
        private readonly ConcurrentDictionary<SubversePeerId, TaskCompletionSource<IList<PeerInfo>>> _getPeersTaskMap;

        private readonly EncryptionKeys _myEntityKeys;
        private readonly PeriodicTimer _timer;

        private readonly SIPUDPChannel _sipChannel;
        private readonly SIPTransport _sipTransport;

        private readonly IDhtEngine _dhtEngine;
        private readonly IDhtListener _dhtListener;

        private readonly HttpClient _http;

        private bool disposedValue;

        public IPEndPoint? LocalEndPoint { get; set; }
        public IPEndPoint? RemoteEndPoint { get; set; }

        public SubversePeerId PeerId { get; }

        public RoutedPeerService(
            ILogger<RoutedPeerService> logger,
            IPgpKeyProvider keyProvider)
        {
            _logger = logger;
            _keyProvider = keyProvider;

            if (!_keyProvider.GetPublicKeyFile().Exists || !_keyProvider.GetPrivateKeyFile().Exists)
            {
                using var pgp = new PGP();
                pgp.GenerateKey(
                    publicKeyFileInfo: _keyProvider.GetPublicKeyFile(),
                    privateKeyFileInfo: _keyProvider.GetPrivateKeyFile(),
                    username: Environment.MachineName,
                    password: _keyProvider.GetPrivateKeyPassPhrase()
                    );
            }

            _myEntityKeys = new EncryptionKeys(
                _keyProvider.GetPublicKeyFile(),
                _keyProvider.GetPrivateKeyFile(),
                _keyProvider.GetPrivateKeyPassPhrase()
                );

            PeerId = new(_myEntityKeys.PublicKey.GetFingerprint());

            _logger.LogInformation(PeerId.ToString());

            _timer = new(DEFAULT_BOOTSTRAP_PERIOD);

            _dhtEngine = new DhtEngine();
            _dhtEngine.PeersFound += DhtPeersFound;

            _dhtListener = new DhtListener(new IPEndPoint(IPAddress.Any, 0));

            _http = new() { BaseAddress = new Uri("https://subverse.network/") };

            LocalEndPoint = _dhtListener.LocalEndPoint;

            _sipChannel = new SIPUDPChannel(IPAddress.Loopback, 5060);
            _sipTransport = new SIPTransport(true, Encoding.UTF8, Encoding.UTF8);
            _sipTransport.AddSIPChannel(_sipChannel);

            _sipTransport.SIPTransportRequestReceived += SipRequestReceived;
            _sipTransport.SIPTransportResponseReceived += SipResponseReceived;

            _callerMap = new ConcurrentDictionary<string, SubversePeerId>();
            _getPeersTaskMap = new ConcurrentDictionary<SubversePeerId, TaskCompletionSource<IList<PeerInfo>>>();
        }

        private void DhtPeersFound(object? sender, PeersFoundEventArgs e)
        {
            if (_getPeersTaskMap.TryRemove(new(e.InfoHash.Span),
                out TaskCompletionSource<IList<PeerInfo>>? tcs))
            {
                tcs.TrySetResult(e.Peers);
            }
        }

        public async Task<bool> InitializeDhtAsync()
        {
            await SynchronizePeersAsync(PeerId);

            await _dhtEngine.SetListenerAsync(_dhtListener);
            await _dhtEngine.StartAsync();

            _dhtEngine.Announce(new(PeerId.GetBytes()), RemoteEndPoint?.Port ?? 0);

            return true;
        }

        private async Task<bool> SynchronizePeersAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                ReadOnlyMemory<byte> nodesBytes = await _dhtEngine.SaveNodesAsync();

                byte[] requestBytes;
                using (PGP pgp = new(_myEntityKeys))
                using (MemoryStream inputStream = new(nodesBytes.ToArray()))
                using (MemoryStream outputStream = new())
                {
                    await pgp.SignAsync(inputStream, outputStream);
                    requestBytes = outputStream.ToArray();
                }

                using (ByteArrayContent requestContent = new(requestBytes)
                { Headers = { ContentType = new("application/octet-stream") } })
                {
                    HttpResponseMessage response = await _http.PostAsync($"nodes?p={PeerId}", requestContent);
                    return await response.Content.ReadFromJsonAsync<bool>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, null);
                return false;
            }
        }

        private async Task SynchronizePeersAsync(SubversePeerId peerId, CancellationToken cancellationToken = default)
        {
            HttpResponseMessage response = await _http.GetAsync($"nodes?p={peerId}", cancellationToken);
            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            _dhtEngine.Add([responseBytes]);
        }

        private async Task SipRequestReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPRequest sipRequest)
        {
            string toEntityStr = sipRequest.Header.To.ToURI.User;
            SubversePeerId toEntityId = SubversePeerId.FromString(toEntityStr);
            _callerMap.TryAdd(sipRequest.Header.CallId, toEntityId);

            _dhtEngine.GetPeers(new(toEntityId.GetBytes()));

            if (toEntityId == PeerId)
            {
                await _sipTransport.SendRequestAsync(
                    new SIPEndPoint(SIPProtocolsEnum.udp, IPAddress.Loopback, 5061),
                    sipRequest);
            }
            else
            {
                TaskCompletionSource<IList<PeerInfo>> tcs = _getPeersTaskMap
                    .GetOrAdd(toEntityId, k => new());
                IList<PeerInfo> peers = await tcs.Task;

                foreach (PeerInfo peer in peers)
                {
                    IPAddress ipAddress = IPAddress.Parse(peer.ConnectionUri.DnsSafeHost);
                    IPEndPoint ipEndPoint = new IPEndPoint(ipAddress, peer.ConnectionUri.Port);

                    await _sipTransport.SendRequestAsync(new SIPEndPoint(ipEndPoint), sipRequest);
                }
            }
        }

        private async Task SipResponseReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPResponse sipResponse)
        {
            if (_callerMap.TryRemove(
                sipResponse.Header.CallId,
                out SubversePeerId fromEntityId) &&
                fromEntityId == PeerId)
            {
                await _sipTransport.SendResponseAsync(
                    new SIPEndPoint(SIPProtocolsEnum.udp, IPAddress.Loopback, 5061),
                    sipResponse);
            }
            else if (fromEntityId != PeerId)
            {
                _dhtEngine.GetPeers(new(fromEntityId.GetBytes()));

                TaskCompletionSource<IList<PeerInfo>> tcs = _getPeersTaskMap
                    .GetOrAdd(fromEntityId, k => new());
                IList<PeerInfo> peers = await tcs.Task;

                foreach (PeerInfo peer in peers)
                {
                    IPAddress ipAddress = IPAddress.Parse(peer.ConnectionUri.DnsSafeHost);
                    IPEndPoint ipEndPoint = new IPEndPoint(ipAddress, peer.ConnectionUri.Port);

                    await _sipTransport.SendResponseAsync(new SIPEndPoint(ipEndPoint), sipResponse);
                }
            }
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                using (FileStream pkFileStream = _keyProvider.GetPublicKeyFile().OpenRead())
                using (StreamContent pkFileStreamContent = new(pkFileStream)
                { Headers = { ContentType = new("application/pgp-keys") } })
                {
                    await _http.PostAsync("pk", pkFileStreamContent);
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    await SynchronizePeersAsync(cancellationToken);
                    await _timer.WaitForNextTickAsync(cancellationToken);

                    foreach (SubversePeerId peer in _callerMap.Values.Distinct())
                    {
                        await SynchronizePeersAsync(peer, cancellationToken);
                        await _timer.WaitForNextTickAsync(cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                await _dhtEngine.StopAsync();
                _sipTransport.Shutdown();
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _dhtEngine.Dispose();
                    _http.Dispose();
                    _sipTransport.Dispose();
                    _timer.Dispose();
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
