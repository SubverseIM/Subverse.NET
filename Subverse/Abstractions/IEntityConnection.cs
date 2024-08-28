using Alethic.Kademlia;
using Subverse.Models;

namespace Subverse.Abstractions
{
    public interface IEntityConnection : IDisposable
    {
        KNodeId160? LocalConnectionId { get; }
        KNodeId160? RemoteConnectionId { get; }

        event EventHandler<MessageReceivedEventArgs> MessageReceived;

        Task CompleteHandshakeAsync(SubverseEntity self);

        Task SendMessageAsync(SubverseMessage message);
    }
}
