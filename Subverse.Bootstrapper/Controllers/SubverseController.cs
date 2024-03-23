using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Subverse.Implementations;
using Subverse.Models;
using System.Collections.Concurrent;

namespace Subverse.Bootstrapper.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class SubverseController : ControllerBase
    {
        private const int DEFAULT_CONFIG_TOPN = 5;

        private readonly int _configTopN;

        private readonly ConcurrentDictionary<string, SubverseHub?> _whitelistedHubs;
        private readonly ConcurrentDictionary<string, DateTime?> _recentlySeenHubs;

        private readonly ILogger<SubverseController> _logger;

        private static IEnumerable<KeyValuePair<string, T?>> GetWhitelistedKeys<T>(IConfiguration configuration)
        {
            return configuration.GetSection("Bootstrapper")?
                .GetSection("Whitelist")?
                .Get<string[]>()?
                .Select(key => new KeyValuePair<string, T?>(key, default)) ??
                Enumerable.Empty<KeyValuePair<string, T?>>();
        }

        public SubverseController(IConfiguration configuration, ILogger<SubverseController> logger)
        {
            _configTopN = configuration.GetSection("Bootstrapper")?.GetValue<int>("TopNListLength") ?? DEFAULT_CONFIG_TOPN;
            _whitelistedHubs = new(GetWhitelistedKeys<SubverseHub>(configuration));
            _recentlySeenHubs = new(GetWhitelistedKeys<DateTime?>(configuration));

            _logger = logger;
        }

        [HttpPost("top")]
        [Consumes("application/octet-stream")]
        [Produces("application/json")]
        public async Task<IResult> ExchangeRecentlySeenPeerInfo()
        {
            byte[] blobBytes;
            using (var memoryStream = new MemoryStream()) 
            {
                await Request.Body.CopyToAsync(memoryStream);
                blobBytes = memoryStream.ToArray();
            }

            var certifiedCookie = CertificateCookie.FromBlobBytes(blobBytes) as CertificateCookie;
            if (certifiedCookie is not null && _whitelistedHubs.ContainsKey(certifiedCookie.Key.ToString()))
            {
                _logger.LogInformation($"Accepting request from claimed identity: {certifiedCookie.Key}");

                _whitelistedHubs[certifiedCookie.Key.ToString()] = certifiedCookie.Body as SubverseHub;
                _recentlySeenHubs[certifiedCookie.Key.ToString()] = DateTime.Now;

                return Results.Json<SubverseHub?[]?>(_whitelistedHubs
                    .Where(x => x.Value is not null)
                    .OrderByDescending(x => _recentlySeenHubs[x.Key] ?? DateTime.MinValue)
                    .Select(x => x.Value)
                    .Take(_configTopN)
                    .ToArray());
            }
            else
            {
                _logger.LogInformation($"Denying request from claimed identity: {certifiedCookie?.Key.ToString() ?? "<NONE>"}");
                return Results.Unauthorized();
            }
        }
    }
}
