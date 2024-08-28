using Subverse.Models;
using System.Net;

namespace Subverse.Abstractions.Server
{
    public interface IHubService
    {
        Task OpenConnectionAsync(IEntityConnection connection);
        Task CloseConnectionAsync(IEntityConnection connection);

        SubverseHub GetSelf();
        void SetLocalEndPoint(IPEndPoint localEndPoint);
    }
}
