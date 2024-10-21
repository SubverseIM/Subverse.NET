using Subverse.Types;

namespace Subverse.Models
{
    public record SubverseMessage(SubversePeerId? Recipient, int TimeToLive, SubverseMessage.ProtocolCode Code, byte[] Content)
    {
        public enum ProtocolCode : short 
        {
            UnsetOrUnknown = 0x0000,
            Error = -0x0100, // SubverseV1::Error
            Command = 0x0100, // SubverseV1::Command
            Entity = 0x0200, // SubverseV1::Entity
            Application = 0x0300, // SubverseV1::Application
        }
    }
}
