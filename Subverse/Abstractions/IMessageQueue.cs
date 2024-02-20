using Subverse.Models;

namespace Subverse.Abstractions
{
    public interface IMessageQueue<TKey> : IDisposable
    {
        public record KeyedMessage(TKey Id, SubverseMessage Message);

        Task EnqueueAsync(TKey key, SubverseMessage message);

        Task<KeyedMessage> DequeueAsync();

        Task<SubverseMessage?> DequeueByKeyAsync(TKey key);
    }
}
