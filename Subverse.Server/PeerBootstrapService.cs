
using Newtonsoft.Json;
using PgpCore;
using Subverse.Abstractions;
using Subverse.Implementations;
using Subverse.Models;
using Subverse.Server;
using System.Net;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

using static Subverse.Models.SubverseMessage;

internal class PeerBootstrapService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<PeerBootstrapService> _logger;
    private readonly IPgpKeyProvider _keyProvider;
    private readonly IPeerService _peerService;

    private readonly string _configApiUrl;
    private readonly HttpClient _http;

    public PeerBootstrapService(IConfiguration configuration, ILogger<PeerBootstrapService> logger, IPgpKeyProvider keyProvider, IPeerService hubService)
    {
        _configuration = configuration;

        _configApiUrl = _configuration.GetConnectionString("BootstrapApi") ??
            throw new ArgumentNullException(message: "Missing required ConnectionString from config: \"BootstrapApi\"", paramName: "configApiUrl");

        _logger = logger;
        _keyProvider = keyProvider;
        _peerService = hubService;

        _http = new HttpClient() { BaseAddress = new(_configApiUrl) };
    }

    private async Task<IEnumerable<(string hostname, IPEndPoint remoteEndpoint)>> BootstrapSelfAsync()
    {
        var selfJsonStr = JsonConvert.SerializeObject(_peerService.GetSelf());
        var selfJsonContent = new StringContent(selfJsonStr, Encoding.UTF8, MediaTypeNames.Application.Json);

        using var apiResponseMessage = await _http.PostAsync("ping", selfJsonContent);
        var apiResponseArray = await apiResponseMessage.Content.ReadFromJsonAsync<SubversePeer[]>();

        var validPeerHostnames = (apiResponseArray ?? [])
            .Select(peer => peer.Hostname);

        var validPeerEndpoints = (apiResponseArray ?? [])
            .Select(peer => peer.DhtUri)
            .Where(uri => uri is not null)
            .Cast<string>()
            .Select(uri => new Uri(uri))
            .Select(uri => new IPEndPoint(
                Dns.GetHostAddresses(
                    uri.DnsSafeHost, AddressFamily.InterNetwork)
                .Single(), uri.Port));

        return validPeerHostnames
            .Zip(validPeerEndpoints);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var (hostname, remoteEndPoint) in await BootstrapSelfAsync())
            {
                try
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    // Try connection w/ 5 second timeout
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5.0)))
                    {
                        var quicConnection = await QuicConnection.ConnectAsync(
                            new QuicClientConnectionOptions
                            {
                                RemoteEndPoint = remoteEndPoint,

                                DefaultStreamErrorCode = 0x0A, // Protocol-dependent error code.
                                DefaultCloseErrorCode = 0x0B, // Protocol-dependent error code.

                                ClientAuthenticationOptions = new()
                                {
                                    ApplicationProtocols = new List<SslApplicationProtocol>() { new("SubverseV2") },
                                    TargetHost = hostname,
                                }
                            }, cts.Token);

                        var peerConnection = new QuicPeerConnection(quicConnection);
                        await _peerService.OpenConnectionAsync(peerConnection, 
                            new SubverseMessage(
                                _peerService.ConnectionId, 
                                0, ProtocolCode.Command, []), 
                            cts.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, null);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5.0));
        }

        _http.Dispose();
    }
}