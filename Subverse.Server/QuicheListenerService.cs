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

        private readonly IHostEnvironment _environment;
        private readonly IConfiguration _configuration;

        private readonly ILogger<QuicheListenerService> _logger;
        private readonly ILoggerProvider _loggerProvider;
        private readonly IPeerService _peerService;

        public QuicheListenerService(IHostEnvironment environment, IConfiguration configuration, ILogger<QuicheListenerService> logger, ILoggerProvider loggerProvider, IPeerService hubService)
        {
            _environment = environment;
            _configuration = configuration;

            _logger = logger;
            _loggerProvider = loggerProvider;
            _peerService = hubService;
        }

        private async Task ListenConnectionsAsync(QuicheConnection quicheConnection, CancellationToken cancellationToken)
        {
            var peerConnection = new QuichePeerConnection(_loggerProvider.CreateLogger("QuicheConnection"), quicheConnection);
            List<SubversePeerId> connectionIds = new();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    SubversePeerId connectionId = await _peerService
                        .OpenConnectionAsync(peerConnection, null, cancellationToken);

                    connectionIds.Add(connectionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, null);
            }

            foreach (var connectionId in connectionIds)
            {
                await _peerService.CloseConnectionAsync(peerConnection, connectionId);
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
                    MaxInitialDataSize = QuicheLibrary.MAX_BUFFER_LEN,

                    MaxInitialBidiStreams = 16,
                    MaxInitialLocalBidiStreamDataSize = QuicheLibrary.MAX_BUFFER_LEN,
                    MaxInitialRemoteBidiStreamDataSize = QuicheLibrary.MAX_BUFFER_LEN,

                    MaxIdleTimeout = 15_000,
                };

                serverConfig.SetApplicationProtocols("SubverseV2");

                string certChainPath = _configuration.GetSection("Privacy")
                    .GetValue<string>("SSLCertChainPath") ?? DEFAULT_CERT_CHAIN_PATH;
                serverConfig.LoadCertificateChainFromPemFile(
                    Path.IsPathFullyQualified(certChainPath) ? certChainPath :
                    Path.Combine(_environment.ContentRootPath, certChainPath)
                    );

                string privateKeyPath = _configuration.GetSection("Privacy")
                    .GetValue<string>("SSLPrivateKeyPath") ?? DEFAULT_PRIVATE_KEY_PATH;
                serverConfig.LoadPrivateKeyFromPemFile(
                    Path.IsPathFullyQualified(privateKeyPath) ? privateKeyPath :
                    Path.Combine(_environment.ContentRootPath, privateKeyPath)
                    );

                List<Task> listenTasks = new();
                try
                {
                    using var socket = new Socket(SocketType.Dgram, ProtocolType.Udp);
                    socket.Bind(new IPEndPoint(IPAddress.Any, 0));

                    _peerService.LocalEndPoint = socket.LocalEndPoint as IPEndPoint;

                    using var listener = new QuicheListener(socket, serverConfig);
                    _ = listener.ListenAsync(stoppingToken);

                    while (!stoppingToken.IsCancellationRequested)
                    {
                        stoppingToken.ThrowIfCancellationRequested();

                        var quicheConnection = await listener.AcceptAsync(stoppingToken);
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
