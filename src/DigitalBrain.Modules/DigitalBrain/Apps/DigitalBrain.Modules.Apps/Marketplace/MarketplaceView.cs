namespace DigitalBrain.Apps;

// The Marketplace as a person browses it: the features folder, then what people published.
// The IMarketplace grain holds only the people's listings.
public sealed class MarketplaceView(IGrainFactory grains, FeatureCatalog catalog)
{
    public async Task<IReadOnlyList<MarketplaceListing>> List(CancellationToken ct)
    {
        var people = await grains.GetGrain<IMarketplace>(IMarketplace.Key).List().WaitAsync(ct);
        return [.. await Shipped(), .. people];
    }

    public async Task<MarketplaceListing?> Read(string publisher, string name, CancellationToken ct)
        => (await Shipped()).FirstOrDefault(listing => listing.Publisher == publisher && listing.Name == name)
            ?? await grains.GetGrain<IMarketplace>(IMarketplace.Key).Read(publisher, name).WaitAsync(ct);

    private Task<IReadOnlyList<MarketplaceListing>> Shipped() => catalog.Listings();
}
