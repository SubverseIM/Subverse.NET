using Alethic.Kademlia;
using Subverse.Abstractions;
using Subverse.Implementations;

namespace Subverse.Models
{
    public record SubverseNode(CookieReference<KNodeId256, CertificateCookie> MostRecentlySeenBy /* HUB */) 
        : SubverseEntity
    {
    }
}