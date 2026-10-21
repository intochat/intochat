using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Marketplace;

namespace IntoChat.OS.Apps;

// A list, not domain state, so it publishes nothing: AppPublished from the app neuron is the
// fact. The catalog itself lives in the trust cluster (R14); this neuron holds nothing and maps
// listings onto the platform's records. Reads are public; writes are checked by
// MarketplaceWriteGuard against the calling app, then signed by the machine for the account that
// owns the listing's source brain.
[GrainType("apps.marketplace")]
internal sealed class MarketplaceNeuron(IMarketplaceClient catalog) : Neuron, IMarketplace
{
    public async Task<MarketplaceListing[]> List() => [.. (await catalog.List()).Select(Listing)];

    public async Task<MarketplaceListing?> Read(string publisher, string name)
        => await catalog.Read(publisher, name) is { } record ? Listing(record) : null;

    public Task Upsert(MarketplaceListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        var record = Record(listing);
        return catalog.Upsert(record.SourceAccount ?? throw new UnauthorizedAccessException("Only a listing published from a brain enters the catalog."), record);
    }

    public async Task Remove(string publisher, string name)
    {
        if (await catalog.Read(publisher, name) is not { SourceAccount: { } account }) { return; }
        await catalog.Remove(account, publisher, name);
    }

    internal static string? OwnerKey(MarketplaceListing listing)
        => listing.Source.Scope() is { } scope ? IApp.Key(scope, listing.Name) : null;

    internal static MarketplaceRecord Record(MarketplaceListing listing)
    {
        var source = listing.Source as AppSource.Brain;
        return new(listing.Publisher, source?.AccountId, source?.BrainId, listing.Name, listing.Title, listing.Description, listing.Version, listing.Icon,
            listing.Requires?.SemanticTypes, listing.Requires?.Connectors, listing.Requires?.Secrets);
    }

    internal static MarketplaceListing Listing(MarketplaceRecord record)
        => new(record.Publisher,
            record is { SourceAccount: { } account, SourceBrain: { } brain } ? new AppSource.Brain(account, brain) : new AppSource.Shipped(),
            record.Name, record.Title, record.Description, record.Version, record.Icon,
            record is { RequiresSemanticTypes: null, RequiresConnectors: null, RequiresSecrets: null } ? null
                : new AppRequirements(record.RequiresSemanticTypes, record.RequiresConnectors, record.RequiresSecrets));
}
