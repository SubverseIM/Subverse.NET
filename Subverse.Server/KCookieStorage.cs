using Alethic.Kademlia;
using Subverse.Abstractions;

internal class KCookieStorage : ICookieStorage<KNodeId256>
{
    private readonly IKLookup<KNodeId256> _kLookup;
    private readonly IKInvoker<KNodeId256> _kInvoker;

    public KCookieStorage(IKLookup<KNodeId256> kLookup, IKInvoker<KNodeId256> kInvoker) 
    {
        _kLookup = kLookup;
        _kInvoker = kInvoker;
    }

    public async Task<TValue?> ReadAsync<TValue>(CookieReference<KNodeId256, TValue> reference, CancellationToken cancellationToken) where TValue : ICookie<KNodeId256>
    {
        var result = await _kLookup.LookupValueAsync(reference.RefersTo, cancellationToken);

        byte[]? blobBytes = result.Value?.Data;
        if (blobBytes is null)
        {
            return default;
        }
        else
        {
            return (TValue)TValue.FromBlobBytes(blobBytes);
        }
    }

    public async Task UpdateAsync<TValue>(CookieReference<KNodeId256, TValue> reference, TValue newValue, CancellationToken cancellationToken) where TValue : ICookie<KNodeId256>
    {
        var result = await _kLookup.LookupValueAsync(reference.RefersTo, cancellationToken);

        byte[] blobBytes = newValue.ToBlobBytes();
        KValueInfo newValueInfo = new(blobBytes, result.Value?.Version + 1 ?? 0, DateTime.UtcNow.AddMinutes(15));

        KStoreRequestMode storeMode = KStoreRequestMode.Primary;
        foreach (var node in result.Nodes)
        {
            await _kInvoker.StoreAsync(node.Endpoints, reference.RefersTo, storeMode, newValueInfo, cancellationToken);
            storeMode = KStoreRequestMode.Replica;
        }
    }
}