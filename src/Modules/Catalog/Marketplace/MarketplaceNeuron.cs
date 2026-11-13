using DigitalBrain.Log;
using IntoChat.OS.Contracts.Configuration;

namespace IntoChat.OS.Modules.Apps;

[GenerateSerializer, Alias("apps.marketplace-state")]
internal sealed record MarketplaceState
{
    [Id(0)] public MarketplaceListing[] Listings { get; init; } = [];
}

[DigitalBrain.Signal("apps.marketplace-changed")]
internal sealed record MarketplaceChanged(int ListingCount) : DigitalBrain.Signal;

/// <summary>
/// Durable cluster-wide Marketplace directory storing community-published listings and portable <see cref="AppPackage"/> definitions.
/// </summary>
[DigitalBrain.Neurons.Neuron("apps.marketplace")]
internal sealed class MarketplaceNeuron : DigitalBrain.Neurons.Neuron<MarketplaceState>, IMarketplace
{
    public Task<MarketplaceListing[]> List()
        => Task.FromResult(State.Listings ?? []);

    public Task<MarketplaceListing?> Read(string publisher, string name)
        => Task.FromResult((State.Listings ?? []).FirstOrDefault(item =>
            string.Equals(item.Publisher, publisher, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)));

    public async Task Upsert(MarketplaceListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentException.ThrowIfNullOrWhiteSpace(listing.Publisher);
        ArgumentException.ThrowIfNullOrWhiteSpace(listing.Name);

        var remaining = (State.Listings ?? [])
            .Where(item => !(string.Equals(item.Publisher, listing.Publisher, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Name, listing.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        remaining.Add(listing);
        MarketplaceListing[] nextListings = [.. remaining.OrderBy(item => item.Publisher, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)];

        await Save(State with { Listings = nextListings }, new MarketplaceChanged(nextListings.Length));
    }

    public async Task Remove(string publisher, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisher);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var existing = State.Listings ?? [];
        var nextListings = existing
            .Where(item => !(string.Equals(item.Publisher, publisher, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (nextListings.Length == existing.Length)
        {
            return;
        }

        await Save(State with { Listings = nextListings }, new MarketplaceChanged(nextListings.Length));
    }

    internal static string? OwnerKey(MarketplaceListing listing)
        => CatalogEntry.ResolveOwnerKey(listing);
}
