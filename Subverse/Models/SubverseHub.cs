using Alethic.Kademlia;
using Subverse.Abstractions;
using Subverse.Implementations;

namespace Subverse.Models
{
    public record SubverseHub(string Hostname, string ServiceUri, string KHostUri, DateTime MostRecentlySeenOn, CookieReference<KNodeId160, CertificateCookie>[] OwningUsers /* USER[] */)
        : SubverseEntity
    {
    }
}
