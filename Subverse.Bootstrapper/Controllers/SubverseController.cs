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

        private static Dictionary<string, SubverseHub?> _whitelistedHubs = new();
        private static Dictionary<string, DateTime?> _recentlySeenHubs = new();

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

            lock (_whitelistedHubs)
            {
                _whitelistedHubs = _whitelistedHubs.Count == 0 ? new(GetWhitelistedKeys<SubverseHub>(configuration)) : _whitelistedHubs;
                _recentlySeenHubs = _recentlySeenHubs.Count == 0 ? new(GetWhitelistedKeys<DateTime?>(configuration)) : _recentlySeenHubs;
            }

            _logger = logger;
        }

        [HttpPost("top")]
        [Consumes("application/octet-stream")]
        [Produces("application/json")]
        public SubverseHub?[]? ExchangeRecentlySeenPeerInfo()
        {
            byte[] blobBytes;
            using (var memoryStream = new MemoryStream()) 
            {
                Request.Body.CopyToAsync(memoryStream).Wait();
                blobBytes = memoryStream.ToArray();
            }

            var certifiedCookie = CertificateCookie.FromBlobBytes(blobBytes) as CertificateCookie;
            if (certifiedCookie is not null && _whitelistedHubs.ContainsKey(certifiedCookie.Key.ToString()))
            {
                _logger.LogInformation($"Accepting request from claimed identity: {certifiedCookie.Key}");

                lock (_whitelistedHubs)
                {
                    _whitelistedHubs[certifiedCookie.Key.ToString()] = certifiedCookie.Body as SubverseHub;
                    _recentlySeenHubs[certifiedCookie.Key.ToString()] = DateTime.Now;

                    return _whitelistedHubs
                        .Where(x => x.Value is not null && x.Key != certifiedCookie.Key.ToString())
                        .OrderByDescending(x => _recentlySeenHubs[x.Key] ?? DateTime.MinValue)
                        .Select(x => x.Value)
                        .Take(_configTopN)
                        .ToArray();
                }
            }
            else
            {
                _logger.LogInformation($"Denying request from claimed identity: {certifiedCookie?.Key.ToString() ?? "<NONE>"}");
                return [];
            }
        }
    }
}
