using Quiche.NET;
using Subverse.Abstractions;
using Subverse.Types;
using System.Buffers;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Subverse.Server
{
    internal class QuicheListenerService : BackgroundService
    {
        private const string DEFAULT_CERT_CHAIN_PATH = "server/conf/cert-chain.pem";
        private const string DEFAULT_PRIVATE_KEY_PATH = "server/conf/private-key.pem";

        private readonly IConfiguration _configuration;
        private readonly IHostEnvironment _environment;
        private readonly ILogger<QuicheListenerService> _logger;
        private readonly IPeerService _peerService;

        private Socket? _socket;

        public QuicheListenerService(IConfiguration configuration, IHostEnvironment environment, ILogger<QuicheListenerService> logger, IPeerService hubService)
        {
            _configuration = configuration;
            _environment = environment;
            _logger = logger;
            _peerService = hubService;
        }

        //private X509Certificate? GetServerCertificate()
        //{
        //    var certPath = _configuration.GetSection("Privacy")
        //        .GetValue<string?>("SSLCertPath") ?? DEFAULT_CERT_PATH;

        //    var certPathFull = Path.IsPathFullyQualified(certPath) ? certPath :
        //        Path.Combine(_environment.ContentRootPath, certPath);

        //    var certPassword = _configuration.GetSection("Privacy")
        //        .GetValue<string?>("SSLCertPassword") ?? DEFAULT_CERT_PASSWORD;

        //    return File.Exists(certPathFull) ? X509CertificateLoader
        //        .LoadPkcs12FromFile(certPathFull, certPassword) : null;
        //}

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
                var initialData = ArrayPool<byte>.Shared.Rent(4096);

                var serverConfig = new QuicheConfig()
                {
                    MaxInitialDataSize = 4096,

                    MaxInitialBidiStreams = 64,
                    MaxInitialLocalBidiStreamDataSize = 4096,
                    MaxInitialRemoteBidiStreamDataSize = 4096,

                    MaxInitialUniStreams = 0,
                    MaxInitialUniStreamDataSize = 0,
                };

                serverConfig.SetApplicationProtocols("SubverseV2");

                serverConfig.LoadCertificateChainFromPemFile(DEFAULT_CERT_CHAIN_PATH);
                serverConfig.LoadPrivateKeyFromPemFile(DEFAULT_PRIVATE_KEY_PATH);

                List<Task> listenTasks = new ();
                try
                {
                    _socket = new Socket(SocketType.Dgram, ProtocolType.Udp);
                    _socket.Bind(new IPEndPoint(IPAddress.Any, 0));

                    _peerService.LocalEndPoint = _socket.LocalEndPoint as IPEndPoint;

                    while (!stoppingToken.IsCancellationRequested)
                    {
                        var result = await _socket.ReceiveFromAsync(initialData, _socket.LocalEndPoint, stoppingToken);
                        var initialDataMem = new ReadOnlyMemory<byte>(initialData, 0, result.ReceivedBytes);

                        var quicheConnection = QuicheConnection.Accept(_socket, result.RemoteEndPoint, initialDataMem, serverConfig);
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
                _socket?.Dispose();
            }
        }
    }
}
