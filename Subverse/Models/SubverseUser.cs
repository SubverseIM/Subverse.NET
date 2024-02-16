using Alethic.Kademlia;
using Subverse.Abstractions;
using Subverse.Implementations;

namespace Subverse.Models
{
    public record SubverseUser(CookieReference<KNodeId256, CertificateCookie>[] OwnedNodes /* NODE[] */)
        : SubverseEntity
    {
    }
}
