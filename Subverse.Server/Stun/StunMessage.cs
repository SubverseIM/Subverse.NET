
namespace Subverse.Stun {
    public class StunMessage {

        public StunMessageHeader Header { get; }
        public List<StunAttribute> Attributes { get; }

        public StunMessage(IEnumerable<StunAttribute> attributes) {
            this.Header = new StunMessageHeader();
            Attributes = new List<StunAttribute>(attributes);
        }

        public StunMessage(Stream stream) {
            this.Header = new StunMessageHeader(stream);
            Attributes = new List<StunAttribute>();

            var pos = 0;
            while (pos < Header.Length) {
                var attr = new StunAttribute(stream, this);
                pos += attr.AttrbiuteLength;
                this.Attributes.Add(attr);
            }
        }

        public byte[] Serialize() {
            var serializedAttributes = this.Attributes.Select(a => a.Serialize());

            this.Header.Length = (ushort)serializedAttributes.Sum(a => a.Length);

            var message = new byte[this.Header.Length + 20];
            var curIndex = 20;

            foreach (var attr in serializedAttributes) {
                Array.Copy(attr, 0, message, curIndex, attr.Length);
                curIndex += attr.Length;
            }

            var header = this.Header.Serialize();
            Array.Copy(header, 0, message, 0, 20);
            return message;
        }

        public override string ToString() => $"{this.Header}\n{string.Join("\n", this.Attributes.Select(a=>a.ToString()))}";

    }
}
