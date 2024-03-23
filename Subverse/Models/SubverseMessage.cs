using Alethic.Kademlia;

namespace Subverse.Models
{
    public record SubverseMessage(KNodeId160[] Tags, int TimeToLive, byte[] Content)
    {
    }
}
