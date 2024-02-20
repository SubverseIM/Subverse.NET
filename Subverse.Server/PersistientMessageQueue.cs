using LiteDB;
using Subverse.Abstractions;
using Subverse.Models;

using static Subverse.Abstractions.IMessageQueue<string>;

namespace Subverse.Server
{
    internal class PersistentMessageQueue : IMessageQueue<string>
    {
        private readonly IConfiguration _configuration;
        private readonly LiteDatabase _db;

        public PersistentMessageQueue(IConfiguration configuration)
        {
            _configuration = configuration;
            _db = new LiteDatabase(_configuration.GetConnectionString("MessageQueueImpl"));
        }

        public Task<KeyedMessage> DequeueAsync()
        {
            var collection = _db.GetCollection<KeyedMessage>();
            var keyedMessage = collection.Query().FirstOrDefault();

            collection.Delete(keyedMessage.Id);
            _db.Commit();

            return Task.FromResult(keyedMessage);
        }

        public Task<SubverseMessage?> DequeueByKeyAsync(string key)
        {
            var collection = _db.GetCollection<KeyedMessage>();
            var keyedMessage = collection.FindById(key);

            collection.Delete(keyedMessage.Id);
            _db.Commit();

            return Task.FromResult<SubverseMessage?>(keyedMessage.Message);
        }

        public Task EnqueueAsync(string key, SubverseMessage message)
        {
            var collection = _db.GetCollection<KeyedMessage>();
            var keyedMessage = new KeyedMessage(key, message);

            collection.Insert(keyedMessage);
            _db.Commit();

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _db.Dispose();
        }
    }
}
