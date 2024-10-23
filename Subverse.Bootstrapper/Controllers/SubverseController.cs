using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using PgpCore;
using System.Text;

namespace Subverse.Bootstrapper.Controllers
{
    [ApiController]
    [Route("/")]
    public class SubverseController : ControllerBase
    {
        private readonly IDistributedCache _cache;
        private readonly ILogger<SubverseController> _logger;

        public SubverseController(IConfiguration configuration, IDistributedCache cache, ILogger<SubverseController> logger)
        {
            _cache = cache;
            _logger = logger;
        }

        [HttpPost("pk")]
        [Consumes("application/pgp-keys")]
        [Produces("application/json")]
        public async Task<bool> SubmitPublicKey(CancellationToken cancellationToken)
        {
            try
            {
                EncryptionKeys keyContainer;
                using (var streamReader = new StreamReader(Request.Body, Encoding.ASCII))
                {
                    keyContainer = new EncryptionKeys(await streamReader.ReadToEndAsync());
                }

                SubversePeerId peerId = new(keyContainer.PublicKey.GetFingerprint());
                _logger.LogInformation($"PKX Submitted: {peerId}");

                await _cache.SetAsync(
                    $"PKX-{peerId}", keyContainer.PublicKey.GetEncoded(),
                    new DistributedCacheEntryOptions { AbsoluteExpiration = null },
                    cancellationToken);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, null);
                return false;
            }
        }

        [HttpGet("nodes")]
        [Produces("application/octet-stream")]
        public async Task<byte[]> GetNodesAsync([FromQuery(Name = "p")] string peerIdStr) 
        {
            return await _cache.GetAsync($"DAT-{peerIdStr}") ?? [];
        }

        [HttpPost("nodes")]
        [Consumes("application/octet-stream")]
        [Produces("application/json")]
        public async Task<bool> PostNodesAsync([FromQuery(Name = "p")] string peerIdStr, CancellationToken cancellationToken)
        {
            try
            {
                byte[]? pkBytes = await _cache.GetAsync($"PKX-{peerIdStr}", cancellationToken);
                if (pkBytes is not null)
                {
                    EncryptionKeys keyContainer;
                    using (MemoryStream ms = new(pkBytes))
                    {
                        keyContainer = new(ms);
                    }

                    byte[] nodesBytes;
                    bool verifySuccess;
                    using (PGP pgp = new(keyContainer))
                    using (MemoryStream inputStream = new())
                    using (MemoryStream outputStream = new())
                    {
                        await Request.Body.CopyToAsync(inputStream);
                        inputStream.Position = 0;

                        verifySuccess = await pgp.VerifyAsync(inputStream, outputStream);
                        nodesBytes = outputStream.ToArray();
                    }

                    if (verifySuccess)
                    {
                        SubversePeerId peerId = new(keyContainer.PublicKey.GetFingerprint());
                        await _cache.SetAsync($"DAT-{peerId}", nodesBytes, 
                            new DistributedCacheEntryOptions { AbsoluteExpiration = null }
                            );
                        return true;
                    }
                }
            }
            catch (Exception ex) 
            {
                _logger.LogError(ex, null);
            }

            return false;
        }
    }
}
