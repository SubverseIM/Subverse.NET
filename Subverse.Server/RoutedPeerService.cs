using MonoTorrent;
using MonoTorrent.Connections.Dht;
using MonoTorrent.Dht;
using PgpCore;
using SIPSorcery.SIP;
using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Subverse.Server
{
    internal class RoutedPeerService : IPeerService
    {
        private readonly ILogger<RoutedPeerService> _logger;
        private readonly IPgpKeyProvider _keyProvider;

        private readonly ConcurrentDictionary<string, SubversePeerId> _callerMap;
        private readonly ConcurrentDictionary<SubversePeerId, TaskCompletionSource<IList<PeerInfo>>> _getPeersTaskMap;

        private readonly EncryptionKeys _myEntityKeys;

        private readonly SIPUDPChannel _sipChannel;
        private readonly SIPTransport _sipTransport;

        private readonly IDhtEngine _dhtEngine;
        private readonly IDhtListener _dhtListener;

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

            _dhtEngine = new DhtEngine();
            _dhtEngine.PeersFound += DhtPeersFound;

            _dhtListener = new DhtListener(new IPEndPoint(IPAddress.Any, 0));

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

        public async Task InitializeDhtAsync()
        {
            await _dhtEngine.SetListenerAsync(_dhtListener);
            await _dhtEngine.StartAsync();

            _dhtEngine.Announce(new(PeerId.GetBytes()), RemoteEndPoint?.Port ?? 0);
        }

        private async Task SipRequestReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPRequest sipRequest)
        {
            string fromEntityStr = sipRequest.Header.From.FromURI.User;
            SubversePeerId fromEntityId = SubversePeerId.FromString(fromEntityStr);
            _callerMap.TryAdd(sipRequest.Header.CallId, fromEntityId);

            string toEntityStr = sipRequest.Header.To.ToURI.User;
            SubversePeerId toEntityId = SubversePeerId.FromString(toEntityStr);
            _dhtEngine.GetPeers(new(toEntityId.GetBytes()));

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

        private async Task SipResponseReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPResponse sipResponse)
        {
            if (_callerMap.TryRemove(
                sipResponse.Header.CallId,
                out SubversePeerId fromEntityId))
            {
                _dhtEngine.GetPeers(new(fromEntityId.GetBytes()));

                TaskCompletionSource<IList<PeerInfo>> tcs = _getPeersTaskMap
                    .AddOrUpdate(fromEntityId, k => new(), (k, old) => new());
                IList<PeerInfo> peers = await tcs.Task;

                foreach (PeerInfo peer in peers)
                {
                    IPAddress ipAddress = IPAddress.Parse(peer.ConnectionUri.DnsSafeHost);
                    IPEndPoint ipEndPoint = new IPEndPoint(ipAddress, peer.ConnectionUri.Port);
                    await _sipTransport.SendResponseAsync(new SIPEndPoint(ipEndPoint), sipResponse);
                }
            }
        }
    }
}
