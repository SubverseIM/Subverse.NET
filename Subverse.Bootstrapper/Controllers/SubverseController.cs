using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Newtonsoft.Json;
using Subverse.Models;
using System.Text;

namespace Subverse.Bootstrapper.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class SubverseController : ControllerBase
    {
        private const string CACHE_KNOWN_PEERS_KEY = "knownPeers";

        private const string CACHE_LOCK_KEY = "__lock__";
        private const int CACHE_LOCK_EXPIRE_MS = 70;
        private const int CACHE_LOCK_WAIT_MS = CACHE_LOCK_EXPIRE_MS + CACHE_LOCK_EXPIRE_MS / 2;

        private const int DEFAULT_CONFIG_TOPN = 5;

        private readonly int _configTopN;
        private readonly IDistributedCache _cache;
        private readonly ILogger<SubverseController> _logger;

        public SubverseController(IConfiguration configuration, IDistributedCache cache, ILogger<SubverseController> logger)
        {
            _configTopN = configuration.GetSection("Bootstrapper")?.GetValue<int?>("TopNListLength") ?? DEFAULT_CONFIG_TOPN;
            _cache = cache;
            _logger = logger;
        }

        private async Task<IEnumerable<T>> AppendWithLockAsync<T>(string key, T value, CancellationToken cancellationToken)
        {
            string lockKey = $"{CACHE_LOCK_KEY}{key}";
            string? lockValue = await _cache.GetStringAsync($"{CACHE_LOCK_KEY}{key}", cancellationToken);

            // wait for lock to expire
            while (lockValue is not null) 
            {
                await Task.Delay(TimeSpan.FromMilliseconds(CACHE_LOCK_WAIT_MS));
                lockValue = await _cache.GetStringAsync(lockKey, cancellationToken);
            }

            await _cache.SetStringAsync($"{CACHE_LOCK_KEY}{key}", Guid.NewGuid().ToString(), 
                new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMilliseconds(CACHE_LOCK_EXPIRE_MS) },
                cancellationToken);

            string? jsonString = await _cache.GetStringAsync(key, cancellationToken);
            HashSet<T> values = JsonConvert.DeserializeObject<HashSet<T>>(jsonString ?? "[]") ?? [];
            values.Add(value);

            await _cache.SetStringAsync(key, JsonConvert.SerializeObject(values));
            return values;
        }

        [HttpPost("ping")]
        [Consumes("application/json")]
        [Produces("application/json")]
        public async Task<SubversePeer[]> ExchangeRecentlySeenPeerInfoAsync(CancellationToken cancellationToken)
        {
            SubversePeer? thisPeer;
            using (var streamReader = new StreamReader(Request.Body, Encoding.UTF8))
            using (var jsonReader = new JsonTextReader(streamReader))
            {
                var serializer = new JsonSerializer();
                thisPeer = serializer.Deserialize<SubversePeer?>(jsonReader);
            }

            if (thisPeer is not null)
            {
                _logger.LogInformation($"Accepting request from host: {thisPeer.Hostname}");

                var thisPeerKey = thisPeer.Hostname;
                var thisPeerJsonStr = JsonConvert.SerializeObject(
                    thisPeer with { MostRecentlySeenOn = DateTime.UtcNow }
                    );

                _cache.SetString(thisPeerKey, thisPeerJsonStr);

                var allPeerKeys = await AppendWithLockAsync(
                    CACHE_KNOWN_PEERS_KEY, thisPeerKey, cancellationToken
                    );
                return allPeerKeys
                    .Where(otherPeerKey => otherPeerKey != thisPeerKey)
                    .Select(otherPeerKey => JsonConvert.DeserializeObject<SubversePeer>(_cache.GetString(otherPeerKey) ?? "null"))
                    .OrderByDescending(x => x?.MostRecentlySeenOn ?? DateTime.MinValue)
                    .Where(x => x is not null)
                    .Cast<SubversePeer>()
                    .Take(_configTopN)
                    .ToArray();
            }
            else
            {
                _logger.LogInformation($"Denying request from unspecified host.");
                return [];
            }
        }
    }
}
