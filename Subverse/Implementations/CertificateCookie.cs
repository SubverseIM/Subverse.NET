using Newtonsoft.Json;
using PgpCore;
using Subverse.Abstractions;
using Subverse.Exceptions;
using Subverse.Models;
using Subverse.Types;
using System.Text;

namespace Subverse.Implementations
{
    public class CertificateCookie : ICookie<SubversePeerId>
    {
        private readonly byte[]? blobBytes;

        public EncryptionKeys KeyContainer { get; }

        public SubversePeerId Key => new(KeyContainer.PublicKey.GetFingerprint());
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
                        Converters = { new PeerIdConverter() }
                    });
            }
            else
            {
                throw new InvalidCookieException($"Signature on {nameof(CertificateCookie)} could not be verified.");
            }

            this.blobBytes = blobBytes;
        }

        public static ICookie<SubversePeerId> FromBlobBytes(byte[] blobBytes)
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
