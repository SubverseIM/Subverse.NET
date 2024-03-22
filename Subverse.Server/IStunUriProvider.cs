namespace Subverse.Server
{
    internal interface IStunUriProvider
    {
        IAsyncEnumerable<string?> GetAvailableAsync();
    }
}
