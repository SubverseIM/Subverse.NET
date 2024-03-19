using System.Net;
using System.Net.Sockets;

namespace Subverse.Stun
{

    public class StunClientUdp {

        public async Task<StunMessage> SendRequestAsync(StunMessage request, int localPortNum, string stunServer) {
            return await this.SendRequestAsync(request, localPortNum, new Uri(stunServer));
        }

        public async Task<StunMessage> SendRequestAsync(StunMessage request, int localPortNum, Uri stunServer) {
            if (stunServer.Scheme == "stuns")
                throw new NotImplementedException("STUN secure is not supported");

            if (stunServer.Scheme != "stun")
                throw new ArgumentException("URI must have stun scheme", nameof(stunServer));

            using(var udp = new UdpClient(localPortNum)) {
                udp.Connect(stunServer.Host, stunServer.Port);
                var requestBytes = request.Serialize();
                var byteCount = await udp.SendAsync(requestBytes, requestBytes.Length);
                var result = await udp.ReceiveAsync();

                using(var stream = new MemoryStream(result.Buffer)) {
                    return new StunMessage(stream);
                }
            }
        }
    }
}
