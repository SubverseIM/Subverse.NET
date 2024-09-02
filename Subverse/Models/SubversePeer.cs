namespace Subverse.Models
{
    public record SubversePeer(string Hostname, string? ServiceUri, DateTime MostRecentlySeenOn)
    {
    }
}
