using Subverse.Models;
using Newtonsoft.Json;
using Org.BouncyCastle.Bcpg;
using PgpCore;
using System.Text;

namespace Subverse.Implementations
{
    public class LocalCertificateCookie : CertificateCookie
    {
        private readonly Stream publicKeyStream;
        private readonly EncryptionKeys privateKeyContainer;

        public LocalCertificateCookie(Stream publicKeyStream, EncryptionKeys privateKeyContainer, SubverseEntity cookieBody) :
            base(new(privateKeyContainer.PublicKey.GetFingerprint()), cookieBody)
        {
            this.publicKeyStream = publicKeyStream;
            this.privateKeyContainer = privateKeyContainer;
        }

        public override byte[] ToBlobBytes()
        {
            using (var outputStreamFull = new MemoryStream())
            {
                publicKeyStream.CopyTo(outputStreamFull);

                string cookieBodyJsonString = JsonConvert.SerializeObject(Body,
                    new JsonSerializerSettings
                    {
                        TypeNameHandling = TypeNameHandling.Objects,
                        Converters = { new NodeIdConverter() }
                    });
                byte[] cookieBodyUtf8Bytes = Encoding.UTF8.GetBytes(cookieBodyJsonString);
                using (var inputStreamBody = new MemoryStream(cookieBodyUtf8Bytes))
                using (var pgp = new PGP(privateKeyContainer))
                {
                    pgp.SignStream(inputStreamBody, outputStreamFull);
                }

                return outputStreamFull.ToArray();
            }
        }
    }
}
