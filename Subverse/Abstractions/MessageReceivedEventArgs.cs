using Subverse.Models;

namespace Subverse.Abstractions
{
    public class MessageReceivedEventArgs : EventArgs 
    { 
        public SubverseMessage Message { get; }

        public MessageReceivedEventArgs(SubverseMessage message) 
        {
            Message = message;
        }
    }
}
