using Alethic.Kademlia;
using Subverse.Abstractions;
using Subverse.Implementations;

namespace Subverse.Models
{
    public record SubverseHub(string Hostname, string ServiceUri, string KHostUri, CookieReference<KNodeId256, CertificateCookie>[] OwningUsers /* USER[] */)
        : SubverseEntity
    {
    }
}
