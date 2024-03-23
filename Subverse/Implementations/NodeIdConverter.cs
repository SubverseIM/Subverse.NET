using Alethic.Kademlia;
using Newtonsoft.Json;
using System.Globalization;

namespace Subverse.Implementations
{
    internal class NodeIdConverter : JsonConverter<KNodeId160>
    {
        private static byte[] StringToByteArray(string hex)
        {
            return Enumerable.Range(0, hex.Length)
                             .Where(x => (x & 1) == 0)
                             .Select(x => byte.Parse(hex.AsSpan().Slice(x, 2), NumberStyles.HexNumber))
                             .ToArray();
        }

        public override KNodeId160 ReadJson(JsonReader reader, Type objectType, KNodeId160 existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            return new KNodeId160(StringToByteArray((string?)reader.Value ?? string.Empty));
        }

        public override void WriteJson(JsonWriter writer, KNodeId160 value, JsonSerializer serializer)
        {
            writer.WriteValue(value.ToString());
        }
    }
}
