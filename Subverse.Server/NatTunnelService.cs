using Mono.Nat;
using System.Net;

namespace Subverse.Server
{
    internal class NatTunnelService : BackgroundService
    {
        private readonly IPeerService _peerService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<NatTunnelService> _logger;

        private readonly TaskCompletionSource _tcs;

        private INatDevice? _natDevice;
        private Mapping? _mapping;

        public NatTunnelService(IPeerService peerService, IConfiguration configuration, ILogger<NatTunnelService> logger)
        {
            _peerService = peerService;
            _configuration = configuration;
            _logger = logger;

            _tcs = new();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_configuration.GetSection("HubService")
                .GetValue<string?>("Hostname") is null) 
            {
                return;
            }

            NatUtility.DeviceFound += NatDeviceFound;
            NatUtility.StartDiscovery();

            try
            {
                await _tcs.Task.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) { }

            try
            {
                if (_natDevice is not null && _mapping is not null)
                {
                    await _natDevice.DeletePortMapAsync(_mapping);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, null);
            }

            NatUtility.StopDiscovery();
        }

        private async void NatDeviceFound(object? sender, DeviceEventArgs e)
        {
            try
            {
                _natDevice = e.Device; 
                _mapping = await _natDevice.CreatePortMapAsync(
                    new(Protocol.Udp, 5060, 5060, 0, "SubverseV2")
                    );

                int remotePort = _mapping.PublicPort;
                IPAddress remoteAddr = await _natDevice.GetExternalIPAsync();

                if (remoteAddr != IPAddress.Any)
                {
                    _peerService.RemoteEndPoint = new IPEndPoint(remoteAddr, remotePort);
                    await _peerService.InitializeDhtAsync();
                }

                _logger.LogInformation($"Successfully allocated external endpoint: {_peerService.RemoteEndPoint}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, null);
            }
        }
    }
}
