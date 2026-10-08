using DigitalBrain.Framework;

namespace DigitalBrain.Apps;

[GenerateSerializer, Alias("apps.marketplace-state")]
public sealed class MarketplaceState
{
    [Id(0)] public MarketplaceListing[] Listings { get; set; } = [];
}

// A list, not domain state, so it publishes nothing: AppPublished from the app neuron is the
// fact. Reads are public; writes are checked by MarketplaceWriteGuard against the calling app.
[GrainType("apps.marketplace")]
internal sealed class MarketplaceNeuron(
    [PersistentState("apps.marketplace", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<MarketplaceState> store)
    : Neuron, IMarketplace
{
    private MarketplaceState State => store.State ??= new();

    public override NeuronAccess Access(string operation)
        => operation is nameof(List) or nameof(Read) ? NeuronAccess.PublicOperation : NeuronAccess.Unclassified;

    public Task<MarketplaceListing[]> List() => Task.FromResult<MarketplaceListing[]>([.. State.Listings]);

    public Task<MarketplaceListing?> Read(string publisher, string name) => Task.FromResult(Find(publisher, name));

    public async Task Upsert(MarketplaceListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        State.Listings = [.. State.Listings.Where(item => !(item.Publisher == listing.Publisher && item.Name == listing.Name))
            .Append(listing)
            .OrderBy(item => item.Publisher, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal)];
        await store.WriteStateAsync();
    }

    public async Task Remove(string publisher, string name)
    {
        if (Find(publisher, name) is null) { return; }
        State.Listings = [.. State.Listings.Where(item => !(item.Publisher == publisher && item.Name == name))];
        await store.WriteStateAsync();
    }

    // The app neuron key that owns a listing, or null when there is no such listing.
    internal string? OwnerKey(string publisher, string name)
        => Find(publisher, name) is { } listing ? OwnerKey(listing) : null;

    internal static string? OwnerKey(MarketplaceListing listing)
        => listing.Source.Scope() is { } scope ? IApp.Key(scope, listing.Name) : null;

    private MarketplaceListing? Find(string publisher, string name)
        => State.Listings.FirstOrDefault(item => item.Publisher == publisher && item.Name == name);
}
