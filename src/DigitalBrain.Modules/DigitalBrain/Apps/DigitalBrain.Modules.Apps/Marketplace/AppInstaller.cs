using DigitalBrain.Framework;

namespace DigitalBrain.Apps;

// The one way an app crosses brains: a listed bundle goes into the caller's brain through the
// feature loader. A person's bundle is read through ExportPublished, which is public; a feature
// comes from the features folder. Install needs no authority beyond the caller's own brain.
public sealed class AppInstaller(IGrainFactory grains, MarketplaceView marketplace, FeatureCatalog catalog, FeatureLoader loader)
{
    public async Task<AppSnapshot> Install(string publisher, string name, CancellationToken ct)
    {
        var scope = BrainScope.CurrentId();
        await catalog.Reload(publisher, name).WaitAsync(ct);
        var listing = await marketplace.Read(publisher, name, ct)
            ?? throw new KeyNotFoundException($"{publisher}/{name} is not in the Marketplace.");
        if (listing.Source.Scope() == scope) { throw new InvalidOperationException($"{name} is already yours: you published it."); }
        var target = grains.GetGrain<IApp>(IApp.Key(scope, name));
        if ((await target.Read().WaitAsync(ct)).Manifest.Title.Length > 0)
        { throw new InvalidOperationException($"You already have an app named {name}."); }
        var bundle = await Bundle(listing.Source, listing.Publisher, name, ct);
        return await loader.Load(scope, name, bundle, new(listing.Publisher, listing.Source, name, bundle.Version), ct);
    }

    public async Task<AppSnapshot> Update(string name, CancellationToken ct)
    {
        var scope = BrainScope.CurrentId();
        var target = grains.GetGrain<IApp>(IApp.Key(scope, name));
        var current = await target.Read().WaitAsync(ct);
        if (current.Origin is not { } origin)
        { throw new InvalidOperationException($"{name} was not installed from the Marketplace."); }
        if (current.Edited)
        { throw new InvalidOperationException($"{name} has your own edits; it no longer takes updates from {origin.Publisher}."); }
        if (origin.Source is AppSource.Shipped) { await catalog.Reload(origin.Publisher, origin.Name).WaitAsync(ct); }
        var listing = await marketplace.Read(origin.Publisher, origin.Name, ct) ?? throw Gone(origin.Source, origin.Publisher, origin.Name);
        if (AppVersions.Compare(listing.Version, origin.Version) <= 0 && !Unconfirmed(current.Status)) { return current; }
        var bundle = await Bundle(origin.Source, origin.Publisher, origin.Name, ct);
        return await loader.Load(scope, name, bundle, origin, ct);
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

    // The published bundle a source holds: a person's brain or the features folder.
    public async Task<AppBundle> Bundle(AppSource source, string publisher, string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source switch
        {
            AppSource.Brain => await grains.GetGrain<IApp>(IApp.Key(source.Scope()!, name)).ExportPublished().WaitAsync(ct),
            AppSource.Shipped => await catalog.Find(publisher, name) ?? throw Gone(source, publisher, name),
            _ => throw new NotSupportedException($"Unknown app source {source.GetType().Name}."),
        };
    }

    // A deploy that failed or never answered: the held program is loaded again until it is Live.
    internal static bool Unconfirmed(AppStatus status) => status is AppStatus.Deploying or AppStatus.Failed;

    private static Exception Gone(AppSource source, string publisher, string name) => source is AppSource.Shipped
        ? new InvalidOperationException($"{publisher}/{name} no longer ships.")
        : new KeyNotFoundException($"{publisher}/{name} is not in the Marketplace.");
}
