using Alethic.Kademlia;
using Subverse.Models;

namespace Subverse.Abstractions
{
    public interface IPeerConnection : IDisposable 
    {
        event EventHandler<MessageReceivedEventArgs> MessageReceived;

        Task<KNodeId160> CompleteHandshakeAsync(SubverseMessage? message, CancellationToken cancellationToken);

        void SendMessage(SubverseMessage message);
    }
}
