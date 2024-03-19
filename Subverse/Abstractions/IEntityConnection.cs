using Alethic.Kademlia;
using Subverse.Models;

namespace Subverse.Abstractions
{
    public interface IEntityConnection : IDisposable
    {
        KNodeId256? ServiceId { get; }
        KNodeId256? ConnectionId { get; }

        event EventHandler<MessageReceivedEventArgs> MessageReceived;

        Task CompleteHandshakeAsync(SubverseEntity self);

        Task SendMessageAsync(SubverseMessage message);
    }
}
