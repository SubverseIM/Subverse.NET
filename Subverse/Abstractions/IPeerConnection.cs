using Subverse.Models;
using Subverse.Types;

namespace Subverse.Abstractions
{
    public interface IPeerConnection : IDisposable 
    {
        event EventHandler<MessageReceivedEventArgs> MessageReceived;

        Task<SubversePeerId> CompleteHandshakeAsync(SubverseMessage? message, CancellationToken cancellationToken);

        void SendMessage(SubverseMessage message);
    }
}
