using Subverse.Models;

namespace Subverse.Abstractions
{
    public interface IMessageQueue<TKey> : IDisposable
        where TKey : unmanaged
    {
        Task EnqueueAsync(TKey key, SubverseMessage message);

        Task<KeyValuePair<TKey, SubverseMessage>?> DequeueAsync();

        Task<SubverseMessage?> DequeueByKeyAsync(TKey key);
    }
}
