using Newtonsoft.Json;
using PgpCore;
using Subverse.Models;
using System.Text;

namespace Subverse.Implementations
{
    public class LocalCertificateCookie : CertificateCookie
    {
        private readonly Stream publicKeyStream;

        public LocalCertificateCookie(Stream publicKeyStream, EncryptionKeys keyContainer, SubversePeer cookieBody) :
            base(keyContainer, cookieBody)
        {
            this.publicKeyStream = publicKeyStream;
        }

        public override byte[] ToBlobBytes()
        {
            List<byte> cookieBytesFull = new();
            using (var memoryStream = new MemoryStream())
            {
                publicKeyStream.Position = 0;
                publicKeyStream.CopyTo(memoryStream);
                cookieBytesFull.AddRange(memoryStream.ToArray());
            }

            string cookieBodyJsonString = JsonConvert.SerializeObject(Body,
                new JsonSerializerSettings
                {
                    TypeNameHandling = TypeNameHandling.Objects,
                    Converters = { new PeerIdConverter() }
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
