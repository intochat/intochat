using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Marketplace;
using DigitalBrain.Authority.Identity.Authority;
using Orleans.Concurrency;

namespace DigitalBrain.Authority.Marketplace;

// Published listings owned by local accounts.
internal interface IMarketplaceCatalog : IGrainWithStringKey
{
    const string Key = "catalog";

    [AlwaysInterleave]
    Task<MarketplaceRecord[]> List();

    [AlwaysInterleave]
    Task<MarketplaceRecord?> Read(string publisher, string name);

    Task Upsert(string account, MarketplaceRecord listing);
    Task Remove(string account, string publisher, string name);

}

[GrainType("platform.marketplace")]
internal sealed class MarketplaceGrain(
    [PersistentState("marketplace", DigitalBrainNames.PlatformGrainStorage)] IPersistentState<MarketplaceCatalogState> store)
    : Grain, IMarketplaceCatalog
{
    private AuthorityNeurons Platform => new(GrainFactory);

    public Task<MarketplaceRecord[]> List() => Task.FromResult<MarketplaceRecord[]>([.. store.State.Listings]);

    public Task<MarketplaceRecord?> Read(string publisher, string name) => Task.FromResult(Find(publisher, name));

    public async Task Upsert(string account, MarketplaceRecord listing)
    {
        await RequireOwner(account, listing);
        store.State.Listings = [.. store.State.Listings.Where(item => !(item.Publisher == listing.Publisher && item.Name == listing.Name)).Append(listing)
            .OrderBy(item => item.Publisher, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal)];
        await store.WriteStateAsync();
    }

    public async Task Remove(string account, string publisher, string name)
    {
        if (Find(publisher, name) is not { } listing) { return; }
        await RequireOwner(account, listing);
        store.State.Listings = [.. store.State.Listings.Where(item => !(item.Publisher == publisher && item.Name == name))];
        await store.WriteStateAsync();
    }


    // Publisher is a principal id and is recorded, not compared; the source brain's owner is what counts.
    private async Task RequireOwner(string account, MarketplaceRecord listing)
    {
        if (listing.SourceAccount != account || string.IsNullOrWhiteSpace(listing.SourceBrain)
            || await Platform.BrainAuthority(BrainScope.Create(listing.SourceAccount, listing.SourceBrain).Id).Account() != account)
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
