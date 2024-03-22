
using PgpCore;
using Subverse.Abstractions.Server;
using Subverse.Implementations;
using Subverse.Models;
using Subverse.Server;
using System.Net;
using System.Net.Http.Json;
using System.Net.Quic;
using System.Net.Security;

internal class HubBootstrapService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly IPgpKeyProvider _keyProvider;
    private readonly IHubService _hubService;

    private readonly string _configApiUrl;
    private readonly HttpClient _http;

    public HubBootstrapService(IConfiguration configuration, IPgpKeyProvider keyProvider, IHubService hubService)
    {
        _configuration = configuration;
        _keyProvider = keyProvider;
        _hubService = hubService;

        _configApiUrl = _configuration.GetConnectionString("BootstrapApi") ??
            throw new ArgumentNullException(message: "Missing ConnectionString from config: \"BootstrapApi\"", paramName: "configApiUrl");
        _http = new HttpClient() { BaseAddress = new(_configApiUrl) };
    }

    private async IAsyncEnumerable<(string hostname, IPEndPoint remoteEndpoint)> BootstrapSelfAsync()
    {
        using (var publicKeyStream = _keyProvider.GetPublicKeyFile().OpenRead())
        {
            var privateKeyContainer = new EncryptionKeys(_keyProvider.GetPrivateKeyFile(), _keyProvider.GetPrivateKeyPassPhrase());
            var certifiedSelf = new LocalCertificateCookie(publicKeyStream, privateKeyContainer, await _hubService.GetSelfAsync());

            using var apiResponseMessage = await _http.PostAsync("/top", new ByteArrayContent(certifiedSelf.ToBlobBytes()));
            using var apiResponseReader = new StreamReader(apiResponseMessage.Content.ReadAsStream());

            string? line;
            while ((line = await apiResponseReader.ReadLineAsync()) is not null)
            {
                var tokens = line.Split(',');
                yield return (tokens[0], IPEndPoint.Parse(tokens[1]));
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await foreach (var (hostname, remoteEndPoint) in BootstrapSelfAsync())
            {
                try
                {
                    // Try connection w/ 5 second timeout
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5.0)))
                    {
#pragma warning disable CA1416 // Validate platform compatibility
                        var quicConnection = await QuicConnection.ConnectAsync(
                            new QuicClientConnectionOptions
                            {
                                RemoteEndPoint = remoteEndPoint,

                                DefaultStreamErrorCode = 0x0A, // Protocol-dependent error code.
                                DefaultCloseErrorCode = 0x0B, // Protocol-dependent error code.

                                ClientAuthenticationOptions = new()
                                {
                                    ApplicationProtocols = new List<SslApplicationProtocol>() { new("SubverseV1") },
                                    TargetHost = hostname,
                                },

                                MaxInboundBidirectionalStreams = 10,
                            }, cts.Token);
#pragma warning restore CA1416 // Validate platform compatibility

                        var hubConnection = new QuicHubConnection(quicConnection, _keyProvider.GetPublicKeyFile(), 
                            _keyProvider.GetPrivateKeyFile(), _keyProvider.GetPrivateKeyPassPhrase());
                        await _hubService.OpenConnectionAsync(hubConnection);
                    }
                }
                catch (OperationCanceledException) { }
            }
        }
    }
}