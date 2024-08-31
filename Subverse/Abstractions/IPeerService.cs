using Alethic.Kademlia;
using Subverse.Models;
using System.Net;

namespace Subverse.Abstractions
{
    public interface IPeerService
    {
        KNodeId160 ConnectionId { get; }

        Task<KNodeId160> OpenConnectionAsync(IPeerConnection connection, SubverseMessage? message, CancellationToken cancellationToken);
        Task CloseConnectionAsync(IPeerConnection connection, KNodeId160 connectionId, CancellationToken cancellationToken);

        SubversePeer GetSelf();
        void SetLocalEndPoint(IPEndPoint localEndPoint);
    }
}
