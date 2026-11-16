using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Runtime.Orleans.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps;

// The Marketplace as a person browses it: the shipped catalog, then what people published.
// The IMarketplace neuron holds only the people's listings.
public sealed class MarketplaceView(IServiceProvider neurons, FeatureCatalog catalog)
{
    public async Task<IReadOnlyList<MarketplaceListing>> List(CancellationToken ct)
    {
        MarketplaceListing[] people;
        var edge = neurons.GetService<EdgeAuthority>();
        using (edge?.Enter(edge.Module("apps.marketplace", new BrainId("system"))))
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
        var edge = neurons.GetService<EdgeAuthority>();
        using (edge?.Enter(edge.Module("apps.marketplace", new BrainId("system"))))
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
