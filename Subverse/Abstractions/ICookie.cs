namespace Subverse.Abstractions
{
    public interface ICookie<TKey>
        where TKey : unmanaged
    {
        TKey Key { get; }

        byte[] ToBlobBytes();
        static abstract ICookie<TKey> FromBlobBytes(byte[] blobBytes);
    }
}
