namespace DigitalBrain.Apps;

// The one load path for a feature bundle into a brain, whatever the source: the features folder,
// a person's Marketplace listing, or an app the Creator published. A new app is imported, an
// existing one has its program replaced (settings, connections and data stay); either way the
// copy remembers where it came from. Whether a load is allowed is the caller's policy.
public sealed class FeatureLoader(IGrainFactory grains, IEnumerable<IFeatureMigration> migrations)
{
    public Task<AppSnapshot> Create(string brain, string name, AppBundle bundle, AppOrigin origin, AppInstallReceipt installation, CancellationToken ct)
        => grains.GetGrain<IApp>(IApp.Key(brain, name)).Import(bundle with { Name = name }, origin, installation).WaitAsync(ct);

    public async Task<AppSnapshot> Load(string brain, string name, AppBundle bundle, AppOrigin origin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(origin);
        foreach (var migration in migrations) { bundle = await migration.BeforeLoad(brain, name, bundle, ct); }
        using var span = FeatureCatalog.Telemetry.StartActivity("feature.assign");
        span?.SetTag("feature.brain", brain);
        span?.SetTag("feature.owner", origin.Publisher);
        span?.SetTag("feature.name", name);
        span?.SetTag("feature.version", bundle.Version);
        var target = grains.GetGrain<IApp>(IApp.Key(brain, name));
        var named = bundle with { Name = name };
        // The origin travels with the program in one write: a deploy that fails or outlasts this
        // call still leaves a copy that knows where it came from, and the next load finishes it.
        var held = origin with { Version = bundle.Version };
        var result = (await target.Read().WaitAsync(ct)).Manifest.Title.Length == 0
            ? await target.Import(named, held).WaitAsync(ct)
            : await target.Replace(named, held).WaitAsync(ct);
        foreach (var migration in migrations) { await migration.AfterLoad(brain, name, bundle, ct); }
        return migrations.Any() ? await target.Read().WaitAsync(ct) : result;
    }
}
