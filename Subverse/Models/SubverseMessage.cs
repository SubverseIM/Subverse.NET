using Alethic.Kademlia;

namespace Subverse.Models
{
    public record SubverseMessage(KNodeId256[] Tags, byte[] Content)
    {
    }
}
