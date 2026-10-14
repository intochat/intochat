using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Marketplace;
using DigitalBrain.Platform.Identity.Authority;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Trust;
using Orleans.Concurrency;

namespace DigitalBrain.Platform.Marketplace;

// The people's Marketplace catalog, in the trust cluster (R14, R56): published brain listings only. A write is
// honoured when a registered machine signed it for the account that owns the listing's source brain (R54).
internal interface IMarketplaceCatalog : IGrainWithStringKey
{
    const string Key = "catalog";

    [AlwaysInterleave]
    Task<MarketplaceRecord[]> List();

    [AlwaysInterleave]
    Task<MarketplaceRecord?> Read(string publisher, string name);

    Task Upsert(MachineSigned<MarketplaceRecord> listing);
    Task UpsertLocal(string account, MarketplaceRecord listing);
    Task RemoveLocal(string account, string publisher, string name);

    Task Remove(MachineSigned<MarketplaceRemoval> removal);
}

[GenerateSerializer, Alias("platform.marketplace.removal")]
internal sealed record MarketplaceRemoval([property: Id(0)] string Publisher, [property: Id(1)] string Name);

[GrainType("platform.marketplace")]
internal sealed class MarketplaceGrain(
    [PersistentState("marketplace", DigitalBrainNames.PlatformGrainStorage)] IPersistentState<MarketplaceCatalogState> store)
    : Grain, IMarketplaceCatalog
{
    public Task<MarketplaceRecord[]> List() => Task.FromResult<MarketplaceRecord[]>([.. store.State.Listings]);

    public Task<MarketplaceRecord?> Read(string publisher, string name) => Task.FromResult(Find(publisher, name));

    public async Task Upsert(MachineSigned<MarketplaceRecord> listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        var account = await Accept(listing.Call());
        await RequireOwner(account, listing.Payload);
        store.State.Listings = [.. store.State.Listings
            .Where(item => !(item.Publisher == listing.Payload.Publisher && item.Name == listing.Payload.Name))
            .Append(listing.Payload)
            .OrderBy(item => item.Publisher, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal)];
        await store.WriteStateAsync();
    }

    public async Task Remove(MachineSigned<MarketplaceRemoval> removal)
    {
        ArgumentNullException.ThrowIfNull(removal);
        var account = await Accept(removal.Call());
        if (Find(removal.Payload.Publisher, removal.Payload.Name) is not { } stored) { return; }
        await RequireOwner(account, stored);
        store.State.Listings = [.. store.State.Listings.Where(item => !(item.Publisher == stored.Publisher && item.Name == stored.Name))];
        await store.WriteStateAsync();
    }

    public async Task UpsertLocal(string account, MarketplaceRecord listing)
    {
        await RequireOwner(account, listing);
        store.State.Listings = [.. store.State.Listings.Where(item => !(item.Publisher == listing.Publisher && item.Name == listing.Name)).Append(listing)
            .OrderBy(item => item.Publisher, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal)];
        await store.WriteStateAsync();
    }

    public async Task RemoveLocal(string account, string publisher, string name)
    {
        if (Find(publisher, name) is not { } listing) { return; }
        await RequireOwner(account, listing);
        store.State.Listings = [.. store.State.Listings.Where(item => !(item.Publisher == publisher && item.Name == name))];
        await store.WriteStateAsync();
    }

    private Task<string> Accept(SignedCall call) => GrainFactory.GetGrain<IMachineRegistry>(call.MachineId).Accept(call);

    // Publisher is a principal id and is recorded, not compared; the source brain's owner is what counts.
    private async Task RequireOwner(string account, MarketplaceRecord listing)
    {
        if (listing.SourceAccount != account || string.IsNullOrWhiteSpace(listing.SourceBrain)
            || await GrainFactory.GetGrain<IBrainAuthority>(BrainScope.Create(listing.SourceAccount, listing.SourceBrain).Id).Account() != account)
        { throw new UnauthorizedAccessException($"Account {account} does not own the source brain of {listing.Publisher}/{listing.Name}."); }
    }

    private MarketplaceRecord? Find(string publisher, string name)
        => store.State.Listings.FirstOrDefault(item => item.Publisher == publisher && item.Name == name);
}

[GenerateSerializer, Alias("platform.marketplace-state")]
internal sealed class MarketplaceCatalogState
{
    [Id(0)] public MarketplaceRecord[] Listings { get; set; } = [];
}
