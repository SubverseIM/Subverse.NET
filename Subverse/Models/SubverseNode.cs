using Alethic.Kademlia;
using Subverse.Abstractions;
using Subverse.Implementations;

namespace Subverse.Models
{
    public record SubverseNode(CookieReference<KNodeId160, CertificateCookie> MostRecentlySeenBy /* HUB */) 
        : SubverseEntity
    {
    }
}