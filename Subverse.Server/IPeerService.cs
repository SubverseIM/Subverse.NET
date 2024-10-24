using System.Net;

namespace Subverse.Server
{
    public interface IPeerService
    {
        SubversePeerId PeerId { get; }

        IPEndPoint? LocalEndPoint { get; set; }
        IPEndPoint? RemoteEndPoint { get; set; }

        Task<bool> InitializeDhtAsync();

        Task RunAsync(CancellationToken cancellationToken);
    }
}
