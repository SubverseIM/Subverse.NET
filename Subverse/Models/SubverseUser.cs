using Alethic.Kademlia;
using Subverse.Abstractions;
using Subverse.Implementations;

namespace Subverse.Models
{
    public record SubverseUser(CookieReference<KNodeId160, CertificateCookie>[] OwnedNodes /* NODE[] */)
        : SubverseEntity
    {
    }
}
