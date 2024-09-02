using System.Globalization;
using System.Text;

namespace Subverse
{
    public static class Utils
    {
        public static Stream ExtractPGPBlockFromStream(Stream inputStream, string blockType) 
        {
            using (var streamReader = new StreamReader(inputStream, Encoding.ASCII, leaveOpen: true))
            {
                return ExtractPGPBlockFromStream(streamReader, blockType);
            }
        }

        public static Stream ExtractPGPBlockFromStream(StreamReader streamReader, string blockType)
        {
            var outputStream = new MemoryStream();
            using (var streamWriter = new StreamWriter(outputStream, Encoding.ASCII, leaveOpen: true))
            {
                string? line;
                while ((line = streamReader.ReadLine()) != $"-----END PGP {blockType}-----")
                {
                    streamWriter.Write(line + "\r\n");
                }
                streamWriter.Write(line + "\r\n");
                streamWriter.Write("\r\n");
                streamWriter.Flush();
            }

            outputStream.Position = 0;
            return outputStream;
        }
    }
}
