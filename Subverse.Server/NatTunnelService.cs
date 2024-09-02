using Mono.Nat;
using Subverse.Abstractions;
using System.Net;

namespace Subverse.Server
{
    internal class NatTunnelService : BackgroundService
    {
        private readonly IPeerService _peerService;
        private readonly ILogger<NatTunnelService> _logger;

        private readonly TaskCompletionSource _tcs;

        private INatDevice? _natDevice;
        private Mapping? _mapping;

        public NatTunnelService(IPeerService peerService, ILogger<NatTunnelService> logger)
        {
            _peerService = peerService;
            _logger = logger;

            _tcs = new();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
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
                if (_peerService.LocalEndPoint is null) return;

                _natDevice = e.Device;

                int localPort = _peerService.LocalEndPoint.Port;
                _mapping = await _natDevice.CreatePortMapAsync(
                    new(Protocol.Udp, localPort, 6_03_03, 0, "SubverseV2")
                    );

                int remotePort = _mapping.PublicPort;
                IPAddress remoteAddr = await _natDevice.GetExternalIPAsync();

                if (remoteAddr != IPAddress.Any)
                {
                    _peerService.RemoteEndPoint = new IPEndPoint(remoteAddr, remotePort);
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
