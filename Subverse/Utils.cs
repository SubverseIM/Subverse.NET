using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Quic;
using System.Text;
using System.Threading.Tasks;

namespace Subverse
{
    public static class Utils
    {
        public static byte[] StringToByteArray(string hex)
        {
            return Enumerable.Range(0, hex.Length)
                             .Where(x => (x & 1) == 0)
                             .Select(x => byte.Parse(hex.AsSpan().Slice(x, 2), NumberStyles.HexNumber))
                             .ToArray();
        }

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
