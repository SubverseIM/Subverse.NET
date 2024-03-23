using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Newtonsoft.Json;
using Subverse.Implementations;
using Subverse.Models;
using System.Linq;

namespace Subverse.Bootstrapper.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class SubverseController : ControllerBase
    {
        private const int DEFAULT_CONFIG_TOPN = 5;

        private readonly int _configTopN;
        private readonly IDistributedCache _cache;
        private readonly ILogger<SubverseController> _logger;
        private readonly string[] _keys;

        private static string[]? GetWhitelistedKeys(IConfiguration configuration)
        {
            return configuration.GetSection("Bootstrapper")?
                .GetSection("Whitelist")?
                .Get<string[]>();
        }

        public SubverseController(IConfiguration configuration, IDistributedCache cache, ILogger<SubverseController> logger)
        {
            _configTopN = configuration.GetSection("Bootstrapper")?.GetValue<int?>("TopNListLength") ?? DEFAULT_CONFIG_TOPN;
            _cache = cache;
            _logger = logger;

            _keys = GetWhitelistedKeys(configuration) ?? [];
        }

        [HttpPost("top")]
        [Consumes("application/octet-stream")]
        [Produces("application/json")]
        public SubverseHub[] ExchangeRecentlySeenPeerInfo()
        {
            byte[] blobBytes;
            using (var memoryStream = new MemoryStream())
            {
                Request.Body.CopyToAsync(memoryStream).Wait();
                blobBytes = memoryStream.ToArray();
            }

            var certifiedCookie = CertificateCookie.FromBlobBytes(blobBytes) as CertificateCookie;
            if (certifiedCookie?.Body is not null && _keys.Contains(certifiedCookie.Key.ToString()))
            {
                _logger.LogInformation($"Accepting request from claimed identity: {certifiedCookie.Key}");

                var cookieKey = certifiedCookie.Key.ToString();
                var cookieBody = (SubverseHub)certifiedCookie.Body with { MostRecentlySeenOn = DateTime.UtcNow };
                var jsonValue = JsonConvert.SerializeObject(cookieBody);

                _cache.SetString(cookieKey, jsonValue);

                return _keys
                    .Where(key => key != cookieKey)
                    .Select(key => JsonConvert.DeserializeObject<SubverseHub>(_cache.GetString(key) ?? "null"))
                    .OrderByDescending(x => x?.MostRecentlySeenOn ?? DateTime.MinValue)
                    .Where(x => x is not null)
                    .Cast<SubverseHub>()
                    .Take(_configTopN)
                    .ToArray();
            }
            else
            {
                _logger.LogInformation($"Denying request from claimed identity: {certifiedCookie?.Key.ToString() ?? "<NONE>"}");
                return [];
            }
        }
    }
}
