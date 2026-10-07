
using DigitalBrain.Framework;

namespace DigitalBrain.Apps;

// The one way an app crosses brains: copy the publisher's published bundle into the caller's
// brain and remember where it came from. The publisher's copy is read through ExportPublished,
// which is public, so Install needs no authority beyond the caller's own brain.
public sealed class AppInstaller(IGrainFactory grains)
{
    public async Task<AppSnapshot> Install(string publisher, string name, CancellationToken ct)
    {
        var scope = BrainScope.CurrentId();
        var listing = await Listing(publisher, name, ct);
        var publisherScope = BrainScope.Create(listing.PublisherAccountId, listing.PublisherBrainId).Id;
        if (publisherScope == scope) { throw new InvalidOperationException($"{name} is already yours: you published it."); }
        var target = grains.GetGrain<IApp>(IApp.Key(scope, name));
        if ((await target.Read().WaitAsync(ct)).Manifest.Title.Length > 0)
        { throw new InvalidOperationException($"You already have an app named {name}."); }
        var bundle = await grains.GetGrain<IApp>(IApp.Key(publisherScope, name)).ExportPublished().WaitAsync(ct);
        await target.Import(bundle).WaitAsync(ct);
        await target.RecordOrigin(new(listing.PublisherAccountId, listing.PublisherBrainId, listing.Publisher, name, bundle.Version)).WaitAsync(ct);
        return await target.Read().WaitAsync(ct);
    }

    public async Task<AppSnapshot> Update(string name, CancellationToken ct)
    {
        var target = grains.GetGrain<IApp>(IApp.Key(BrainScope.CurrentId(), name));
        var current = await target.Read().WaitAsync(ct);
        if (current.Origin is not { } origin)
        { throw new InvalidOperationException($"{name} was not installed from the Marketplace."); }
        if (current.Edited)
        { throw new InvalidOperationException($"{name} has your own edits; it no longer takes updates from {origin.Publisher}."); }
        var listing = await Listing(origin.Publisher, origin.Name, ct);
        if (AppVersions.Compare(listing.Version, origin.Version) <= 0) { return current; }
        var bundle = await grains.GetGrain<IApp>(IApp.Key(BrainScope.Create(origin.PublisherAccountId, origin.PublisherBrainId).Id, origin.Name))
            .ExportPublished().WaitAsync(ct);
        // Replace swaps the program in place: settings, connections and stored data stay.
        await target.Replace(bundle with { Name = name }).WaitAsync(ct);
        await target.RecordOrigin(origin with { Version = bundle.Version }).WaitAsync(ct);
        return await target.Read().WaitAsync(ct);
    }

    public async Task<MarketplaceEntry> Entry(MarketplaceListing listing, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(listing);
        var scope = BrainScope.CurrentId();
        if (BrainScope.Create(listing.PublisherAccountId, listing.PublisherBrainId).Id == scope) { return new(listing, true, false); }
        var local = await grains.GetGrain<IApp>(IApp.Key(scope, listing.Name)).Read().WaitAsync(ct);
        var installed = local.Origin is { } origin && origin.Publisher == listing.Publisher && origin.Name == listing.Name;
        var update = installed && !local.Edited && AppVersions.Compare(listing.Version, local.Origin!.Version) > 0;
        return new(listing, installed, update);
    }

    private async Task<MarketplaceListing> Listing(string publisher, string name, CancellationToken ct)
        => await grains.GetGrain<IMarketplace>(IMarketplace.Key).Read(publisher, name).WaitAsync(ct)
            ?? throw new KeyNotFoundException($"{publisher}/{name} is not in the Marketplace.");
}
