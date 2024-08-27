using Cogito.IO;
using Newtonsoft.Json;
using PgpCore;
using Subverse.Models;
using System.Text;

namespace Subverse.Implementations
{
    public class LocalCertificateCookie : CertificateCookie
    {
        private readonly Stream publicKeyStream;

        public LocalCertificateCookie(Stream publicKeyStream, EncryptionKeys keyContainer, SubverseEntity cookieBody) :
            base(keyContainer, cookieBody)
        {
            this.publicKeyStream = publicKeyStream;
        }

        public override byte[] ToBlobBytes()
        {
            publicKeyStream.Position = 0;
            List<byte> cookieBytesFull = publicKeyStream.ReadAllBytes().ToList();

            string cookieBodyJsonString = JsonConvert.SerializeObject(Body,
                new JsonSerializerSettings
                {
                    TypeNameHandling = TypeNameHandling.Objects,
                    Converters = { new NodeIdConverter() }
                });
            byte[] cookieBodyUtf8Bytes = Encoding.UTF8.GetBytes(cookieBodyJsonString);
            using (var inputStreamBody = new MemoryStream(cookieBodyUtf8Bytes))
            using (var outputStream = new MemoryStream())
            using (var pgp = new PGP(KeyContainer))
            {
                pgp.SignStream(inputStreamBody, outputStream);
                cookieBytesFull.AddRange(outputStream.ToArray());
            }

            return cookieBytesFull.ToArray();
        }
    }
}
