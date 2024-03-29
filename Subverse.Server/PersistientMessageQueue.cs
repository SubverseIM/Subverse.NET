﻿using LiteDB;
using Subverse.Abstractions;
using Subverse.Models;

using static Subverse.Abstractions.IMessageQueue<string>;

namespace Subverse.Server
{
    internal class PersistentMessageQueue : IMessageQueue<string>
    {
        private const string DEFAULT_CONFIG_MSG_QUEUE_PATH = "server/msg-queue.litedb";

        private readonly IConfiguration _configuration;
        private readonly IHostEnvironment _environment;

        private readonly LiteDatabase _db;

        public PersistentMessageQueue(IConfiguration configuration, IHostEnvironment environment)
        {
            _configuration = configuration;
            _environment = environment;

            var userPath = _configuration.GetConnectionString("MessageQueueImpl") ?? DEFAULT_CONFIG_MSG_QUEUE_PATH;
            // Init LiteDB instance; create index over KeyedMessage.Key for efficient search!
            _db = new LiteDatabase(Path.IsPathFullyQualified(userPath) ? userPath :
                Path.Combine(_environment.ContentRootPath, userPath)
                );
            _db.GetCollection<KeyedMessage>().EnsureIndex(x => x.Key);
        }

        public Task<KeyedMessage?> DequeueAsync()
        {
            var collection = _db.GetCollection<KeyedMessage>();
            var keyedMessage = collection.FindAll().First();

            if (keyedMessage is not null)
            {
                collection.Delete(keyedMessage.Id);
            }

            return Task.FromResult(keyedMessage);
        }

        public Task<SubverseMessage?> DequeueByKeyAsync(string key)
        {
            var collection = _db.GetCollection<KeyedMessage>();
            var keyedMessage = collection.FindOne(x => x.Key == key);

            if (keyedMessage is not null)
            {
                collection.Delete(keyedMessage.Id);
            }

            return Task.FromResult(keyedMessage?.Message);
        }

        public Task EnqueueAsync(string key, SubverseMessage message)
        {
            var collection = _db.GetCollection<KeyedMessage>();
            var keyedMessage = new KeyedMessage(0, key, message);

            collection.Insert(keyedMessage);

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _db.Dispose();
        }
    }
}
