using Subverse.Models;

namespace Subverse.Abstractions.Server
{
    public interface IHubService
    {
        Task OpenConnectionAsync(IEntityConnection connection);
        Task CloseConnectionAsync(IEntityConnection connection);

        Task<SubverseHub> GetSelfAsync();
    }
}
