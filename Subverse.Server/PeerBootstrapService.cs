
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
    private static readonly TimeSpan DEFAULT_BOOTSTRAP_PEER_PERIOD = TimeSpan.FromSeconds(10.0);
    private static readonly TimeSpan DEFAULT_BOOTSTRAP_PEER_TIMEOUT = TimeSpan.FromSeconds(5.0);
    private static readonly TimeSpan DEFAULT_BOOTSTRAP_REQUEST_TIMEOUT = TimeSpan.FromSeconds(15.0);

    private const string DEFAULT_CONFIG_BOOTSTRAP_API = "https://subverse.network/";

    private readonly IConfiguration _configuration;
    private readonly ILogger<PeerBootstrapService> _logger;
    private readonly IPeerService _peerService;

    private readonly HttpClient _http;
    private readonly PeriodicTimer _timer;

    private readonly ConcurrentDictionary<string, IPeerConnection> _connectionMap;

    private readonly string _configApiUrl;

    private bool disposedValue;

    public PeerBootstrapService(IConfiguration configuration, ILogger<PeerBootstrapService> logger, IPeerService hubService)
    {
        _configuration = configuration;

        _configApiUrl = _configuration.GetConnectionString("BootstrapApi") 
            ?? DEFAULT_CONFIG_BOOTSTRAP_API;

        _logger = logger;
        _peerService = hubService;

        _http = new HttpClient()
        {
            BaseAddress = new(_configApiUrl),
            Timeout = DEFAULT_BOOTSTRAP_REQUEST_TIMEOUT
        };

        _timer = new PeriodicTimer(DEFAULT_BOOTSTRAP_PEER_PERIOD);

        _connectionMap = new();
    }

    private async Task<IEnumerable<(string hostname, IPEndPoint? remoteEndpoint)>> BootstrapSelfAsync(CancellationToken cancellationToken)
    {
        var selfJsonStr = JsonConvert.SerializeObject(_peerService.GetSelf());
        var selfJsonContent = new StringContent(selfJsonStr, Encoding.UTF8, MediaTypeNames.Application.Json);

        SubversePeer[]? apiResponseArray = null;
        try
        {
            using var apiResponseMessage = await _http.PostAsync("ping", selfJsonContent, cancellationToken);
            apiResponseArray = await apiResponseMessage.Content.ReadFromJsonAsync<SubversePeer[]>(cancellationToken);
        }
        catch (HttpRequestException) { }
        catch (System.Text.Json.JsonException) { }
        catch (OperationCanceledException) { }

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
            stoppingToken.ThrowIfCancellationRequested();

            foreach (var (hostname, remoteEndPoint) in await BootstrapSelfAsync(stoppingToken))
            {
                await _timer.WaitForNextTickAsync(stoppingToken);

                if (remoteEndPoint is null ||
                        _connectionMap.TryGetValue(hostname, out IPeerConnection? currentPeerConnection) &&
                        !currentPeerConnection.HasValidConnectionTo(_peerService.ConnectionId) &&
                        _connectionMap.TryRemove(hostname, out IPeerConnection? _))
                {
                    continue;
                }

                // Try connection w/ timeout
                try
                {
                    using CancellationTokenSource cts = new(DEFAULT_BOOTSTRAP_PEER_TIMEOUT);
                    QuicConnection quicConnection = await QuicConnection.ConnectAsync(
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

                            KeepAliveInterval = TimeSpan.FromSeconds(1.0),
                        }, cts.Token);

                    QuicPeerConnection peerConnection = new(quicConnection);
                    _connectionMap.AddOrUpdate(hostname, peerConnection,
                        (key, oldConnection) =>
                        {
                            _peerService.CloseConnectionAsync(oldConnection,
                                _peerService.ConnectionId, cts.Token).Wait();
                            return peerConnection;
                        });

                    await _peerService.OpenConnectionAsync(peerConnection,
                        new SubverseMessage(_peerService.ConnectionId,
                        0, ProtocolCode.Command, []), cts.Token);
                }
                catch (QuicException ex) { _logger.LogError(ex, null); }
                catch (OperationCanceledException ex) { _logger.LogError(ex, null); }
            }
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                _connectionMap.Clear();

                _http.Dispose();
                _timer.Dispose();
            }

            disposedValue = true;
        }
    }

    public override void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}