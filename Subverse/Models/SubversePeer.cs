namespace Subverse.Models
{
    public record SubversePeer(string Hostname, string? DhtUri, DateTime MostRecentlySeenOn)
    {
    }
}
