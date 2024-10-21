using Newtonsoft.Json;
using Subverse.Types;
using System.Globalization;

namespace Subverse.Implementations
{
    public class PeerIdConverter : JsonConverter<SubversePeerId?>
    {
        private static byte[] StringToByteArray(string hex)
        {
            return Enumerable.Range(0, hex.Length)
                             .Where(x => (x & 1) == 0)
                             .Select(x => byte.Parse(hex.AsSpan().Slice(x, 2), NumberStyles.HexNumber))
                             .ToArray();
        }

        public override SubversePeerId? ReadJson(JsonReader reader, Type objectType, SubversePeerId? existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            string? tokenOrNull = (string?)reader.Value;
            return tokenOrNull is null ? null : new SubversePeerId(StringToByteArray(tokenOrNull));
        }

        public override void WriteJson(JsonWriter writer, SubversePeerId? value, JsonSerializer serializer)
        {
            writer.WriteValue(value?.ToString());
        }
    }
}
