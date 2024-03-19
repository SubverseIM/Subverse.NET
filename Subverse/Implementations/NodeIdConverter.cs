using Alethic.Kademlia;
using Newtonsoft.Json;
using System.Globalization;

namespace Subverse.Implementations
{
    internal class NodeIdConverter : JsonConverter<KNodeId256>
    {
        private static byte[] StringToByteArray(string hex)
        {
            return Enumerable.Range(0, hex.Length)
                             .Where(x => (x & 1) == 0)
                             .Select(x => byte.Parse(hex.AsSpan().Slice(x, 2), NumberStyles.HexNumber))
                             .ToArray();
        }

        public override KNodeId256 ReadJson(JsonReader reader, Type objectType, KNodeId256 existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            return new KNodeId256(StringToByteArray((string?)reader.Value ?? string.Empty));
        }

        public override void WriteJson(JsonWriter writer, KNodeId256 value, JsonSerializer serializer)
        {
            writer.WriteValue(value.ToString());
        }
    }
}
