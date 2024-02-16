namespace Subverse.Abstractions
{
    public record struct CookieReference<TKey, TValue>(TKey RefersTo)
        where TKey : unmanaged
        where TValue : ICookie<TKey>
    {
        public Task<TValue> ResolveAsync(ICookieStorage<TKey> storage, CancellationToken cancellationToken) => storage.ReadAsync(this, cancellationToken);
    }
}
