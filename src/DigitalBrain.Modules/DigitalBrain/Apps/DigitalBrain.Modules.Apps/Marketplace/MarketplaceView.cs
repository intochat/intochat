namespace DigitalBrain.Apps;

// The Marketplace as a person browses it: the catalog the image ships, then what people published.
// The IMarketplace grain holds only the people's listings.
public sealed class MarketplaceView(IGrainFactory grains, ShippedCatalog? catalog = null)
{
    public async Task<IReadOnlyList<MarketplaceListing>> List(CancellationToken ct)
    {
        var people = await grains.GetGrain<IMarketplace>(IMarketplace.Key).List().WaitAsync(ct);
        return [.. await Shipped(), .. people];
    }

    public async Task<MarketplaceListing?> Read(string publisher, string name, CancellationToken ct)
        => (await Shipped()).FirstOrDefault(listing => listing.Publisher == publisher && listing.Name == name)
            ?? await grains.GetGrain<IMarketplace>(IMarketplace.Key).Read(publisher, name).WaitAsync(ct);

    private async Task<IReadOnlyList<MarketplaceListing>> Shipped()
        => catalog is null ? [] : await catalog.Listings();
}
