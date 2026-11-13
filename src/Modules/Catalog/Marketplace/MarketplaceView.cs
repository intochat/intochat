using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Modules.Apps;

// The Marketplace as a person browses it: the shipped catalog, then what people published.
// The IMarketplace neuron holds only the people's listings.
public sealed class MarketplaceView(IServiceProvider neurons, FeatureCatalog catalog)
{
    public async Task<IReadOnlyList<MarketplaceListing>> List(CancellationToken ct)
    {
        MarketplaceListing[] people;
        using (InProcessCallerScope.ClearCaller())
        {
            people = await SiloNeurons.Get<IMarketplace>(neurons, IMarketplace.Key).List().WaitAsync(ct);
        }
        return [.. await Shipped(), .. people];
    }

    public async Task<MarketplaceListing?> Read(string publisher, string name, CancellationToken ct)
    {
        if ((await Shipped()).FirstOrDefault(listing => listing.Publisher == publisher && listing.Name == name) is { } shipped)
        {
            return shipped;
        }
        using (InProcessCallerScope.ClearCaller())
        {
            return await SiloNeurons.Get<IMarketplace>(neurons, IMarketplace.Key).Read(publisher, name).WaitAsync(ct);
        }
    }

    private async Task<IReadOnlyList<MarketplaceListing>> Shipped()
    {
        var compiled = await catalog.Listings();
        var seen = compiled.Select(item => (item.Publisher, item.Name)).ToHashSet();
        var extra = ShippedPackages.All
            .Where(pkg => !seen.Contains((ShippedPackages.Publisher, pkg.Key)))
            .Select(pkg => pkg.ToMarketplaceListing());
        return [.. compiled, .. extra];
    }
}
