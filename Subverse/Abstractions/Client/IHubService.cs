using Subverse.Implementations;

namespace Subverse.Abstractions.Client
{
    public interface IHubService
    {
        Task<IEntityConnection> ConnectAsAsync(LocalCertificateCookie localCertificate);
    }
}
