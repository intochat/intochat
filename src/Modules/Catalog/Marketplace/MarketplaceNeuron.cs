using IntoChat.OS.Contracts.Configuration;

namespace IntoChat.OS.Modules.Apps;

// Marketplace returns in 0.8.0. The neuron stays so the shell's catalog route still resolves.
[DigitalBrain.Neurons.Neuron("apps.marketplace")]
internal sealed class MarketplaceNeuron : DigitalBrain.Neurons.Neuron, IMarketplace
{
    public Task<MarketplaceListing[]> List() => Task.FromResult(Array.Empty<MarketplaceListing>());

    public Task<MarketplaceListing?> Read(string publisher, string name) => Task.FromResult<MarketplaceListing?>(null);

    public Task Upsert(MarketplaceListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        throw new NotSupportedException("Marketplace returns in 0.8.0.");
    }

    public Task Remove(string publisher, string name) => Task.CompletedTask;

    internal static string? OwnerKey(MarketplaceListing listing)
        => CatalogEntry.ResolveOwnerKey(listing);
}
