using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Newtonsoft.Json;
using Subverse.Models;
using System.Net;
using System.Text;

namespace Subverse.Bootstrapper.Controllers
{
    [ApiController]
    [Route("/")]
    public class SubverseController : ControllerBase
    {
        private const string CACHE_KNOWN_PEERS_KEY = "KNOWN_PEERS";

        private const string CACHE_LOCK_KEY = "LOCK_";
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
            string? lockValue = await _cache.GetStringAsync($"{CACHE_LOCK_KEY}{key}");

            // wait for lock to expire
            for (int i = 0; i < 10 && lockValue is not null; i++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(CACHE_LOCK_WAIT_MS), cancellationToken);
                lockValue = await _cache.GetStringAsync(lockKey);
            }

            await _cache.SetStringAsync($"{CACHE_LOCK_KEY}{key}", Guid.NewGuid().ToString(),
                new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMilliseconds(CACHE_LOCK_EXPIRE_MS) });

            string? jsonString = await _cache.GetStringAsync(key);
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
            {
                string bodyJson = await streamReader.ReadToEndAsync();
                thisPeer = JsonConvert.DeserializeObject<SubversePeer?>(bodyJson);
            }

            if (thisPeer is not null)
            {
                _logger.LogInformation($"Accepting request from host: {thisPeer.Hostname}");

                string thisPeerKey = thisPeer.Hostname;
                string thisPeerJsonStr = JsonConvert.SerializeObject(
                    thisPeer with { MostRecentlySeenOn = DateTime.UtcNow }
                    );

                await _cache.SetStringAsync(thisPeerKey, thisPeerJsonStr,
                    new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromSeconds(90.0) });

                IEnumerable<string> allPeerKeys = await AppendWithLockAsync(
                    CACHE_KNOWN_PEERS_KEY, thisPeerKey, cancellationToken
                    );
                return allPeerKeys
                    .Where(otherPeerKey => otherPeerKey != thisPeerKey)
                    .Select(otherPeerKey => JsonConvert.DeserializeObject<SubversePeer>(_cache.GetString(otherPeerKey) ?? "null"))
                    .OrderByDescending(x => x?.MostRecentlySeenOn ?? DateTime.MinValue)
                    .Where(x => x is not null)
                    .Cast<SubversePeer>()
                    .Where(peer =>
                    {
                        if (!string.IsNullOrWhiteSpace(peer.ServiceUri))
                        {
                            try
                            {
                                Uri uri = new (peer.ServiceUri);
                                return uri.Port >= 0 && uri.DnsSafeHost.Any();
                            }
                            catch (UriFormatException) { }
                        }

                        return false;
                    })
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
