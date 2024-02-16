using Alethic.Kademlia;
using Alethic.Kademlia.Network;
using Subverse.Abstractions;
using Subverse.Implementations;

namespace Subverse.Models
{
    public record SubverseHub(KIpEndpoint ServiceEndpoint, CookieReference<KNodeId256, CertificateCookie>[] OwningUsers /* USER[] */)
        : SubverseEntity
    {
    }
}
