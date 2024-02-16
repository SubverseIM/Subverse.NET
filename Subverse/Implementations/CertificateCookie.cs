using Alethic.Kademlia;
using Subverse.Abstractions;
using Subverse.Exceptions;
using Subverse.Models;
using Newtonsoft.Json;
using PgpCore;
using System.Text;

namespace Subverse.Implementations
{
    public class CertificateCookie : ICookie<KNodeId256>
    {
        private readonly byte[]? blobBytes;

        public KNodeId256 Key { get; }
        public SubverseEntity? Body { get; }

        protected CertificateCookie(KNodeId256 key, SubverseEntity body)
        {
            Key = key;
            Body = body;
        }

        private CertificateCookie(EncryptionKeys publicKeyContainer, string cookieBody, byte[] blobBytes)
        {
            using var pgp = new PGP(publicKeyContainer);

            var result = pgp.VerifyAndReadSignedArmoredString(cookieBody);
            if (result.IsVerified)
            {
                Key = new(publicKeyContainer.PublicKey.GetFingerprint());
                Body = JsonConvert.DeserializeObject<SubverseEntity>(cookieBody,
                    new JsonSerializerSettings
                    {
                        TypeNameHandling = TypeNameHandling.Auto
                    });
            }
            else
            {
                throw new InvalidCookieException($"Signature on {nameof(CertificateCookie)} could not be verified.");
            }

            this.blobBytes = blobBytes;
        }

        public static ICookie<KNodeId256> FromBlobBytes(byte[] blobBytes)
        {
            using (var memoryStream = new MemoryStream(blobBytes))
            using (var streamReader = new StreamReader(memoryStream, Encoding.UTF8))
            {
                var publicKeyContainer = new EncryptionKeys(memoryStream);
                var cookieBody = streamReader.ReadToEnd();

                return new CertificateCookie(publicKeyContainer, cookieBody, blobBytes);
            }
        }

        public virtual byte[] ToBlobBytes()
        {
            return blobBytes ?? throw new InvalidCookieException("Blob bytes are unavailable for this cookie instance.");
        }
    }
}
