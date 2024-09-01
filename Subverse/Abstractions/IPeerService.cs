using Subverse.Models;
using Subverse.Types;
using System.Net;

namespace Subverse.Abstractions
{
    public interface IPeerService
    {
        SubversePeerId ConnectionId { get; }

        Task<SubversePeerId> OpenConnectionAsync(IPeerConnection connection, SubverseMessage? message, CancellationToken cancellationToken);
        Task CloseConnectionAsync(IPeerConnection connection, SubversePeerId connectionId, CancellationToken cancellationToken);

        SubversePeer GetSelf();
        void SetLocalEndPoint(IPEndPoint localEndPoint);
    }
}
