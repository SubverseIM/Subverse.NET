using Subverse.Models;
using Subverse.Types;
using System.Net;

namespace Subverse.Abstractions
{
    public interface IPeerService
    {
        SubversePeerId PeerId { get; }

        Task<SubversePeerId> OpenConnectionAsync(IPeerConnection connection, SubverseMessage? message, CancellationToken cancellationToken = default);
        Task CloseConnectionAsync(IPeerConnection connection, SubversePeerId connectionId, CancellationToken cancellationToken = default);

        SubversePeer GetSelf();

        IPEndPoint? LocalEndPoint { get; set; }
        IPEndPoint? RemoteEndPoint { get; set; }
    }
}
