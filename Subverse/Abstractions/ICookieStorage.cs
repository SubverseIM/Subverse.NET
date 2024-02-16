namespace Subverse.Abstractions
{
    public interface ICookieStorage<TKey>
        where TKey : unmanaged
    {
        Task<TValue?> ReadAsync<TValue>(CookieReference<TKey, TValue> reference, CancellationToken cancellationToken)
            where TValue : ICookie<TKey>;
        Task UpdateAsync<TValue>(CookieReference<TKey, TValue> reference, TValue newValue, CancellationToken cancellationToken) 
            where TValue : ICookie<TKey>;
    }
}
