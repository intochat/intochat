using DigitalBrain.Framework;

namespace DigitalBrain.Apps;

// The one way an app crosses brains: copy a listed bundle into the caller's brain and remember
// where it came from. A person's bundle is read through ExportPublished, which is public; a shipped
// one comes from the catalog. Install needs no authority beyond the caller's own brain.
public sealed class AppInstaller(IGrainFactory grains, MarketplaceView marketplace, ShippedCatalog? catalog = null)
{
    public async Task<AppSnapshot> Install(string publisher, string name, CancellationToken ct)
    {
        var scope = BrainScope.CurrentId();
        var listing = await marketplace.Read(publisher, name, ct)
            ?? throw new KeyNotFoundException($"{publisher}/{name} is not in the Marketplace.");
        if (listing.Source.Scope() == scope) { throw new InvalidOperationException($"{name} is already yours: you published it."); }
        var target = grains.GetGrain<IApp>(IApp.Key(scope, name));
        if ((await target.Read().WaitAsync(ct)).Manifest.Title.Length > 0)
        { throw new InvalidOperationException($"You already have an app named {name}."); }
        var bundle = await Bundle(listing.Source, listing.Publisher, name, ct);
        await target.Import(bundle).WaitAsync(ct);
        await target.RecordOrigin(new(listing.Publisher, listing.Source, name, bundle.Version)).WaitAsync(ct);
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
        var listing = await marketplace.Read(origin.Publisher, origin.Name, ct) ?? throw Gone(origin.Source, origin.Publisher, origin.Name);
        if (AppVersions.Compare(listing.Version, origin.Version) <= 0) { return current; }
        var bundle = await Bundle(origin.Source, origin.Publisher, origin.Name, ct);
        // Replace swaps the program in place: settings, connections and stored data stay.
        await target.Replace(bundle with { Name = name }).WaitAsync(ct);
        await target.RecordOrigin(origin with { Version = bundle.Version }).WaitAsync(ct);
        return await target.Read().WaitAsync(ct);
    }

    public async Task<MarketplaceEntry> Entry(MarketplaceListing listing, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(listing);
        var scope = BrainScope.CurrentId();
        if (listing.Source.Scope() == scope) { return new(listing, true, false); }
        var local = await grains.GetGrain<IApp>(IApp.Key(scope, listing.Name)).Read().WaitAsync(ct);
        var installed = local.Origin is { } origin && origin.Publisher == listing.Publisher && origin.Name == listing.Name;
        var update = installed && !local.Edited && AppVersions.Compare(listing.Version, local.Origin!.Version) > 0;
        return new(listing, installed, update);
    }

    // The published bundle a source holds: a person's brain or the shipped catalog.
    public async Task<AppBundle> Bundle(AppSource source, string publisher, string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source switch
        {
            AppSource.Brain => await grains.GetGrain<IApp>(IApp.Key(source.Scope()!, name)).ExportPublished().WaitAsync(ct),
            AppSource.Shipped => (catalog is null ? null : await catalog.Find(publisher, name)) ?? throw Gone(source, publisher, name),
            _ => throw new NotSupportedException($"Unknown app source {source.GetType().Name}."),
        };
    }

    private static Exception Gone(AppSource source, string publisher, string name) => source is AppSource.Shipped
        ? new InvalidOperationException($"{publisher}/{name} no longer ships.")
        : new KeyNotFoundException($"{publisher}/{name} is not in the Marketplace.");
}
