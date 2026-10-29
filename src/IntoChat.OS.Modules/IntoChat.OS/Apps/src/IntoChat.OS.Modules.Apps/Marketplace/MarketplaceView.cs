using DigitalBrain.Runtime.Orleans.Hosting;

namespace IntoChat.OS.Modules.Apps;

// The Marketplace as a person browses it: the features folder, then what people published.
// The IMarketplace neuron holds only the people's listings.
public sealed class MarketplaceView(IServiceProvider neurons, FeatureCatalog catalog)
{
    public async Task<IReadOnlyList<MarketplaceListing>> List(CancellationToken ct)
    {
        var people = await SiloNeurons.Get<IMarketplace>(neurons, IMarketplace.Key).List().WaitAsync(ct);
        return [.. await Shipped(), .. people];
    }

    public async Task<MarketplaceListing?> Read(string publisher, string name, CancellationToken ct)
        => (await Shipped()).FirstOrDefault(listing => listing.Publisher == publisher && listing.Name == name)
            ?? await SiloNeurons.Get<IMarketplace>(neurons, IMarketplace.Key).Read(publisher, name).WaitAsync(ct);

    private Task<IReadOnlyList<MarketplaceListing>> Shipped() => catalog.Listings();
}
