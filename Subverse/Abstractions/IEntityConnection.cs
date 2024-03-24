using Alethic.Kademlia;
using Subverse.Models;

namespace Subverse.Abstractions
{
    public interface IEntityConnection : IDisposable
    {
        KNodeId160? ServiceId { get; }
        KNodeId160? ConnectionId { get; }

        event EventHandler<MessageReceivedEventArgs> MessageReceived;

        Task CompleteHandshakeAsync(SubverseEntity self);

        Task SendMessageAsync(SubverseMessage message);
    }
}
