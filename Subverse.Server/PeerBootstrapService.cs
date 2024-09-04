
using Newtonsoft.Json;
using Subverse.Abstractions;
using Subverse.Models;
using Subverse.Server;
using System.Collections.Concurrent;
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
    private readonly IPeerService _peerService;

    private readonly ConcurrentDictionary<string, IPeerConnection> _connectionMap;
    private readonly string _configApiUrl;
    private readonly HttpClient _http;

    public PeerBootstrapService(IConfiguration configuration, ILogger<PeerBootstrapService> logger, IPeerService hubService)
    {
        _configuration = configuration;

        _configApiUrl = _configuration.GetConnectionString("BootstrapApi") ??
            throw new ArgumentNullException(message: "Missing required ConnectionString from config: \"BootstrapApi\"", paramName: "configApiUrl");

        _logger = logger;
        _peerService = hubService;

        _http = new HttpClient() { BaseAddress = new(_configApiUrl) };

        _connectionMap = new ();
    }

    private async Task<IEnumerable<(string hostname, IPEndPoint? remoteEndpoint)>> BootstrapSelfAsync()
    {
        var selfJsonStr = JsonConvert.SerializeObject(_peerService.GetSelf());
        var selfJsonContent = new StringContent(selfJsonStr, Encoding.UTF8, MediaTypeNames.Application.Json);

        using var apiResponseMessage = await _http.PostAsync("ping", selfJsonContent);
        var apiResponseArray = await apiResponseMessage.Content.ReadFromJsonAsync<SubversePeer[]>();

        var validPeerHostnames = (apiResponseArray ?? [])
            .Select(peer => peer.Hostname);

        var validPeerEndpoints = (apiResponseArray ?? [])
            .Select(peer => 
            {
                if (!string.IsNullOrWhiteSpace(peer.ServiceUri)) 
                {
                    try
                    {
                        return new Uri(peer.ServiceUri);
                    }
                    catch (UriFormatException) { }
                }
                return null;
            })
            .Select(uri => 
            {
                if (uri is not null)
                {
                    try
                    {
                        IPAddress? hostAddress = Dns.GetHostAddresses(uri.DnsSafeHost)
                            .SingleOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                        return hostAddress is null ? null : new IPEndPoint(hostAddress, uri.Port);
                    }
                    catch (ArgumentException) { }
                    catch (SocketException) { }
                }

                return null;
            });

        return validPeerHostnames
            .Zip(validPeerEndpoints);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var (hostname, remoteEndPoint) in await BootstrapSelfAsync())
            {
                if (remoteEndPoint is null) continue;

                try
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    if (_connectionMap.TryGetValue(hostname, out IPeerConnection? currentPeerConnection) && 
                        currentPeerConnection.HasValidConnectionTo(_peerService.ConnectionId)) 
                    {
                        continue;
                    }

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
                                },

                                MaxInboundBidirectionalStreams = 64,

                                KeepAliveInterval = TimeSpan.FromSeconds(5.0),
                            }, cts.Token);

                        var peerConnection = new QuicPeerConnection(quicConnection);
                        _connectionMap.AddOrUpdate(hostname, peerConnection, 
                            (key, oldConnection) => 
                            {
                                oldConnection.Dispose();
                                return peerConnection;
                            });

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
        }

        _http.Dispose();
    }
}