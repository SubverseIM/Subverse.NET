using Alethic.Kademlia;

namespace Subverse.Models
{
    public record SubverseMessage(KNodeId256[] Tags, int TimeToLive, byte[] Content)
    {
    }
}
