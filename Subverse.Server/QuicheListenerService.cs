using Quiche.NET;
using Subverse.Abstractions;
using Subverse.Types;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Subverse.Server
{
    internal class QuicheListenerService : BackgroundService
    {
        private const string DEFAULT_CERT_CHAIN_PATH = "server/conf/cert-chain.pem";
        private const string DEFAULT_PRIVATE_KEY_PATH = "server/conf/private-key.pem";

        private readonly ILogger<QuicheListenerService> _logger;
        private readonly IPeerService _peerService;

        public QuicheListenerService(ILogger<QuicheListenerService> logger, IPeerService hubService)
        {
            _logger = logger;
            _peerService = hubService;
        }

        private async Task ListenConnectionsAsync(QuicheConnection quicheConnection, CancellationToken cancellationToken)
        {
            var peerConnection = new QuichePeerConnection(quicheConnection);
            List<SubversePeerId> connectionIds = new();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    connectionIds.Add(await _peerService.OpenConnectionAsync(
                        peerConnection, null, cancellationToken));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, null);
            }

            foreach (var connectionId in connectionIds)
            {
                await _peerService.CloseConnectionAsync(
                    peerConnection, connectionId
                    );
            }

            quicheConnection.Dispose();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var initialData = new byte[QuicheLibrary.MAX_DATAGRAM_LEN];

                var serverConfig = new QuicheConfig()
                {
                    MaxInitialDataSize = QuicheLibrary.MAX_DATAGRAM_LEN,

                    MaxInitialBidiStreams = 64,
                    MaxInitialLocalBidiStreamDataSize = QuicheLibrary.MAX_DATAGRAM_LEN,
                    MaxInitialRemoteBidiStreamDataSize = QuicheLibrary.MAX_DATAGRAM_LEN,

                    MaxInitialUniStreams = 64,
                    MaxInitialUniStreamDataSize = QuicheLibrary.MAX_DATAGRAM_LEN,
                };

                serverConfig.SetApplicationProtocols("SubverseV2");

                serverConfig.LoadCertificateChainFromPemFile(DEFAULT_CERT_CHAIN_PATH);
                serverConfig.LoadPrivateKeyFromPemFile(DEFAULT_PRIVATE_KEY_PATH);

                List<Task> listenTasks = new ();
                try
                {
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        var socket = new Socket(SocketType.Dgram, ProtocolType.Udp);
                        socket.Bind(new IPEndPoint(IPAddress.Any, 0));

                        _peerService.LocalEndPoint = socket.LocalEndPoint as IPEndPoint;

                        var result = await socket.ReceiveFromAsync(initialData, socket.LocalEndPoint ?? 
                            throw new ArgumentNullException(), stoppingToken);

                        var initialDataMem = new ReadOnlyMemory<byte>(initialData, 0, result.ReceivedBytes);

                        var quicheConnection = QuicheConnection.Accept(socket, result.RemoteEndPoint, initialDataMem, serverConfig);
                        var listenTask = Task.Run(() => ListenConnectionsAsync(quicheConnection, stoppingToken));
                        listenTasks.Add(listenTask);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, null);
                }

                await Task.WhenAll(listenTasks);
            }
        }
    }
}
