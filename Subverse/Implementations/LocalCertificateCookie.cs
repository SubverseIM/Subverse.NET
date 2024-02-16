using Subverse.Models;
using Newtonsoft.Json;
using Org.BouncyCastle.Bcpg;
using PgpCore;
using System.Text;

namespace Subverse.Implementations
{
    public class LocalCertificateCookie : CertificateCookie
    {
        private readonly EncryptionKeys privateKeyContainer;

        public LocalCertificateCookie(EncryptionKeys privateKeyContainer, SubverseEntity cookieBody) :
            base(new(privateKeyContainer.PublicKey.GetFingerprint()), cookieBody)
        {
            this.privateKeyContainer = privateKeyContainer;
        }

        public override byte[] ToBlobBytes()
        {
            using (var outputStreamFull = new MemoryStream())
            {
                using (var armoredOutputStream = new ArmoredOutputStream(outputStreamFull))
                {
                    privateKeyContainer.PublicKey.Encode(armoredOutputStream);
                }

                string cookieBodyJsonString = JsonConvert.SerializeObject(Body,
                    new JsonSerializerSettings
                    {
                        TypeNameHandling = TypeNameHandling.Auto,
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
