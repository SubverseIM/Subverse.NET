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

        public EncryptionKeys KeyContainer { get; }

        public KNodeId160 Key => new(KeyContainer.PublicKey.GetFingerprint());
        public SubversePeer? Body { get; }

        protected CertificateCookie(EncryptionKeys keyContainer, SubversePeer body)
        {
            KeyContainer = keyContainer;
            Body = body;
        }

        private CertificateCookie(EncryptionKeys keyContainer, string cookieBody, byte[] blobBytes)
        {
            using var pgp = new PGP(keyContainer);

            var result = pgp.VerifyAndReadSignedArmoredString(cookieBody);
            if (result.IsVerified)
            {
                KeyContainer = keyContainer;
                Body = JsonConvert.DeserializeObject<SubversePeer>(result.ClearText,
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
                var keyContainer = new EncryptionKeys(publicKeyStream);
                var cookieBody = bodyReader.ReadToEnd();

                return new CertificateCookie(keyContainer, cookieBody, blobBytes);
            }
        }

        public virtual byte[] ToBlobBytes()
        {
            return blobBytes ?? throw new InvalidCookieException("Blob bytes are unavailable for this cookie instance.");
        }
    }
}
