using Subverse.Abstractions;
using Subverse.Abstractions.Server;
using Subverse.Stun;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Subverse.Server
{
#pragma warning disable CA1416 // Validate platform compatibility
    internal class QuicListenerService : BackgroundService
    {
        private const string DEFAULT_CERT_PATH = "./server/conf/server.pfx";

        private readonly ILogger<QuicListenerService> _logger;

        private readonly IConfiguration _configuration;
        private readonly IPgpKeyProvider _keyProvider;
        private readonly IHubService _hubService;

        private QuicListener? _listener;

        public QuicListenerService(ILogger<QuicListenerService> logger, IConfiguration configuration, IPgpKeyProvider keyProvider, IHubService hubService)
        {
            _logger = logger;
            _configuration = configuration;
            _keyProvider = keyProvider;
            _hubService = hubService;
        }

        private X509Certificate GetServerCertificate()
        {
            var certPath = _configuration.GetSection("Privacy").GetValue<string>("SSLCertPath");
            var certPassword = _configuration.GetSection("Privacy").GetValue<string>("SSLCertPassword");
            return new X509Certificate2(certPath ?? DEFAULT_CERT_PATH, certPassword);
        }

        private async Task ListenConnectionsAsync(QuicConnection quicConnection, CancellationToken cancellationToken)
        {
            var connectionList = new List<IEntityConnection>();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var quicStream = await quicConnection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);

                    var entityConnection = new QuicEntityConnection(quicStream, _keyProvider.GetPublicKeyFile(), _keyProvider.GetPrivateKeyFile(), _keyProvider.GetPrivateKeyPassPhrase());
                    connectionList.Add(entityConnection);

                    await _hubService.OpenConnectionAsync(entityConnection);
                }
            }
            finally
            {
                foreach (var entityConnection in connectionList)
                {
                    await _hubService.CloseConnectionAsync(entityConnection);
                }

                await quicConnection.DisposeAsync();
            }
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
                        ApplicationProtocols = new List<SslApplicationProtocol>() { new("SubverseV1") },
                        ServerCertificate = GetServerCertificate()
                    },

                    MaxInboundBidirectionalStreams = 10
                };

                _hubService.SetLocalEndPoint(new IPEndPoint(IPAddress.Any, 30603));
                _hubService.GetSelf();

                _listener = await QuicListener.ListenAsync(
                    new QuicListenerOptions
                    {
                        ListenEndPoint = new IPEndPoint(IPAddress.Any, 30603),
                        ApplicationProtocols = new List<SslApplicationProtocol>() { new("SubverseV1") },
                        ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(serverConnectionOptions)
                    });

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
                finally
                {
                    await Task.WhenAll(listenTasks);
                    await _listener.DisposeAsync();
                }
            }
        }
    }
#pragma warning restore CA1416 // Validate platform compatibility
}
