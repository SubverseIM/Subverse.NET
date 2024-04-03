using Alethic.Kademlia;
using Subverse.Abstractions;
using Subverse.Exceptions;
using Subverse.Models;
using Newtonsoft.Json;
using PgpCore;
using System.Text;

namespace Subverse.Implementations
{
    public class CertificateCookie : ICookie<KNodeId160>
    {
        private readonly byte[]? blobBytes;

        public KNodeId160 Key { get; }
        public SubverseEntity? Body { get; }

        protected CertificateCookie(KNodeId160 key, SubverseEntity body)
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
                byte[] fingerprint = publicKeyContainer.PublicKey.GetFingerprint();
                Key = new(fingerprint);
                Body = JsonConvert.DeserializeObject<SubverseEntity>(result.ClearText,
                    new JsonSerializerSettings
                    {
                        TypeNameHandling = TypeNameHandling.Objects,
                        Converters = { new NodeIdConverter() }
                    });
            }
            else
            {
                throw new InvalidCookieException($"Signature on {nameof(CertificateCookie)} could not be verified.");
            }

            this.blobBytes = blobBytes;
        }

        public static ICookie<KNodeId160> FromBlobBytes(byte[] blobBytes)
        {
            using (var bodyStream = new MemoryStream(blobBytes))
            using (var bodyReader = new StreamReader(bodyStream, Encoding.ASCII))
            using (var publicKeyStream = Utils.ExtractPGPBlockFromStream(bodyReader, "PUBLIC KEY BLOCK"))
            {
                var publicKeyContainer = new EncryptionKeys(publicKeyStream);
                var cookieBody = bodyReader.ReadToEnd();

                return new CertificateCookie(publicKeyContainer, cookieBody, blobBytes);
            }
        }

        public virtual byte[] ToBlobBytes()
        {
            return blobBytes ?? throw new InvalidCookieException("Blob bytes are unavailable for this cookie instance.");
        }
    }
}
