using System;
using System.Diagnostics;
using System.Net;

namespace Subverse.Stun {

    public enum AddressFamily : byte {
        IPv4 = 0x01,
        IPv6 = 0x02,
    }

    /// <summary>
    /// The MAPPED-ADDRESS attribute indicates a reflexive transport address
    /// of the client.  It consists of an 8-bit address family and a 16-bit
    /// port, followed by a fixed-length value representing the IP address.
    /// If the address family is IPv4, the address MUST be 32 bits.  If the
    /// address family is IPv6, the address MUST be 128 bits.  All fields
    /// must be in network byte order.
    /// 
    /// https://tools.ietf.org/html/rfc8489#section-14.1
    /// </summary>
    public static class StunAttributeMappedAddress {

        public static IPEndPoint GetMappedAddress(this StunAttribute attribute) {
            var variable = attribute.Variable;

            var family = (AddressFamily)variable[1];
            var port = (ushort)((variable[2] << 8) | variable[3]);
            var addressSize = variable.Length - sizeof(ushort) * 2;

            var addressBytes = new byte[addressSize];
            Array.Copy(variable, 4, addressBytes, 0, addressBytes.Length);
            var ipAddress = new IPAddress(addressBytes);

            return new IPEndPoint(ipAddress, port);
        }

        public static void SetMappedAddress(this StunAttribute attribute, IPEndPoint endPoint) {
            throw new NotImplementedException();
        }

    }
}
