using Subverse.Abstractions;
using Subverse.Implementations;
using Subverse.Types;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Subverse.Server
{
    internal class QuicListenerService : BackgroundService
    {
        private const string DEFAULT_CERT_PATH = "server/conf/server.pfx";

        private readonly IConfiguration _configuration;
        private readonly IHostEnvironment _environment;
        private readonly ILogger<QuicListenerService> _logger;
        private readonly IPeerService _hubService;

        private QuicListener? _listener;

        public QuicListenerService(IConfiguration configuration, IHostEnvironment environment, ILogger<QuicListenerService> logger, IPeerService hubService)
        {
            _configuration = configuration;
            _environment = environment;
            _logger = logger;
            _hubService = hubService;
        }

        private X509Certificate GetServerCertificate()
        {
            var certPath = _configuration.GetSection("Privacy").GetValue<string>("SSLCertPath") ?? DEFAULT_CERT_PATH;
            var certPassword = _configuration.GetSection("Privacy").GetValue<string>("SSLCertPassword");
            return new X509Certificate2(Path.IsPathFullyQualified(certPath) ? certPath :
                Path.Combine(_environment.ContentRootPath, certPath), certPassword);
        }

        private async Task ListenConnectionsAsync(QuicConnection quicConnection, CancellationToken cancellationToken)
        {
            var peerConnection = new QuicPeerConnection(quicConnection);
            List<SubversePeerId> connectionIds = new();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    connectionIds.Add(await _hubService.OpenConnectionAsync(
                        peerConnection, null, cancellationToken));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, null);
            }

            foreach (var connectionId in connectionIds)
            {
                await _hubService.CloseConnectionAsync(
                    peerConnection, connectionId, default
                    );
            }

            await quicConnection.DisposeAsync();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var serverConnectionOptions = new QuicServerConnectionOptions
                {
                    DefaultStreamErrorCode = 0x0A, // Protocol-dependent error code.

                    DefaultCloseErrorCode = 0x0B, // Protocol-dependent error code.

                    ServerAuthenticationOptions = new SslServerAuthenticationOptions
                    {
                        ApplicationProtocols = new List<SslApplicationProtocol>() { new("SubverseV2") },
                        ServerCertificate = GetServerCertificate()
                    },

                    MaxInboundUnidirectionalStreams = 64,
                };

                _listener = await QuicListener.ListenAsync(
                    new QuicListenerOptions
                    {
                        ListenEndPoint = new IPEndPoint(IPAddress.Any, 0),
                        ApplicationProtocols = new List<SslApplicationProtocol>() { new("SubverseV2") },
                        ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(serverConnectionOptions)
                    });

                _hubService.SetLocalEndPoint(_listener.LocalEndPoint);
                _hubService.GetSelf();

                var listenTasks = new List<Task>();
                try
                {
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        var quicConnection = await _listener.AcceptConnectionAsync(stoppingToken);
                        var listenTask = Task.Run(() => ListenConnectionsAsync(quicConnection, stoppingToken));
                        listenTasks.Add(listenTask);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, null);
                }

                await Task.WhenAll(listenTasks);
                await _listener.DisposeAsync();
            }
        }
    }
}
