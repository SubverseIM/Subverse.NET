using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Subverse.Server
{
    /// <summary>
    /// Represents a generic 160-bit Peer ID.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    public unsafe struct SubversePeerId : IEquatable<SubversePeerId>, IComparable<SubversePeerId>
    {
        public static SubversePeerId FromString(string hex)
        {
            return new(Enumerable.Range(0, hex.Length)
                             .Where(x => (x & 1) == 0)
                             .Select(x => byte.Parse(hex.AsSpan().Slice(x, 2), NumberStyles.HexNumber))
                             .ToArray());
        }

        const int SIZE_BYTES = 20;
        const int SIZE_DWORD = SIZE_BYTES / sizeof(int);

        [FieldOffset(0)]
        fixed int data[SIZE_DWORD];

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <param name="c"></param>
        public SubversePeerId(ulong a, ulong b, uint c)
        {
            fixed (int* ptr = data)
            {
                var s = new Span<byte>(ptr, SIZE_BYTES);
                BinaryPrimitives.WriteUInt64BigEndian(s, a);
                BinaryPrimitives.WriteUInt64BigEndian(s = s.Slice(sizeof(ulong)), b);
                BinaryPrimitives.WriteUInt32BigEndian(s.Slice(sizeof(ulong)), c);
            }
        }

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <param name="c"></param>
        /// <param name="d"></param>
        /// <param name="e"></param>
        public SubversePeerId(uint a, uint b, uint c, uint d, uint e) :
            this(((ulong)a << 32) + b, ((ulong)c << 32) + d, e)
        {

        }

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="id"></param>
        public SubversePeerId(ReadOnlySpan<byte> id)
        {
            fixed (int* d = data)
                id.CopyTo(new Span<byte>(d, SIZE_BYTES));
        }

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="id"></param>
        public SubversePeerId(ReadOnlySpan<uint> id) :
            this(id[0], id[1], id[2], id[3], id[4])
        {

        }

        public ReadOnlySpan<byte> GetBytes()
        {
            fixed (void* d = data)
                return new ReadOnlySpan<byte>(d, SIZE_BYTES);
        }

        public override string ToString()
        {
            Span<char> result = stackalloc char[SIZE_BYTES * 2];
            fixed (int* ptr = data)
            {
                var s = new ReadOnlySpan<byte>(ptr, SIZE_BYTES);
                for (int i = 0; i < SIZE_BYTES; i++)
                {
                    s[i].ToString("x2").CopyTo(result.Slice(i << 1, 2));
                }
            }
            return new string(result);
        }

        public override int GetHashCode()
        {
            fixed (int* ptr = data)
            {
                return HashCode.Combine(ptr[0], ptr[1], ptr[2], ptr[3], ptr[4]);
            }
        }

        public override bool Equals(object? obj)
        {
            if (obj is SubversePeerId other)
            {
                return Equals(other);
            }
            else
            {
                return false;
            }
        }

        public bool Equals(SubversePeerId other)
        {
            fixed (int* lptr = data)
            {
                var l = new ReadOnlySpan<int>(lptr, SIZE_DWORD);
                var r = new ReadOnlySpan<int>(other.data, SIZE_DWORD);
                return l.SequenceEqual(r);
            }
        }

        public static bool operator ==(SubversePeerId left, SubversePeerId right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(SubversePeerId left, SubversePeerId right)
        {
            return !(left == right);
        }

        public int CompareTo(SubversePeerId other)
        {
            fixed (int* lptr = data)
            {
                var l = new ReadOnlySpan<int>(lptr, SIZE_DWORD);
                var r = new ReadOnlySpan<int>(other.data, SIZE_DWORD);
                return l.SequenceCompareTo(r);
            }
        }

        public static bool operator <(SubversePeerId left, SubversePeerId right)
        {
            return left.CompareTo(right) < 0;
        }

        public static bool operator <=(SubversePeerId left, SubversePeerId right)
        {
            return left.CompareTo(right) <= 0;
        }

        public static bool operator >(SubversePeerId left, SubversePeerId right)
        {
            return left.CompareTo(right) > 0;
        }

        public static bool operator >=(SubversePeerId left, SubversePeerId right)
        {
            return left.CompareTo(right) >= 0;
        }
    }
}
