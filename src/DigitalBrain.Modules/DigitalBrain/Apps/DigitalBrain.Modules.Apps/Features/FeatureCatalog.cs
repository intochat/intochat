using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Apps;

// The features the host loads from its features folder (DigitalBrain:Modules:apps:Options:Features):
// code, not content anyone wrote at runtime. Loaded at boot and on first read; only Save to source
// reloads it. A bundle that does not compile never reaches the catalog, so loading is where a
// feature passes the publish gate. A host with no folder configured has no features.
public sealed class FeatureCatalog
{
    internal static readonly ActivitySource Telemetry = new("DigitalBrain.Apps.Features");

    private readonly FeatureDirectory? _directory;
    private readonly IScriptSandbox? _sandbox;
    private readonly Lock _gate = new();
    private Task<IReadOnlyDictionary<(string Owner, string Name), AppBundle>>? _bundles;

    public FeatureCatalog(FeatureDirectory? directory, IScriptSandbox? sandbox = null)
    {
        _directory = directory;
        _sandbox = sandbox;
    }

    // A relative folder resolves against the host's base directory, where a published image carries it.
    internal static FeatureCatalog From(IServiceProvider services)
    {
        var path = services.GetRequiredService<IOptions<AppsOptions>>().Value.Features;
        var development = services.GetService<IHostEnvironment>()?.IsDevelopment() == true;
        var directory = string.IsNullOrWhiteSpace(path) ? null
            : new FeatureDirectory(System.IO.Path.GetFullPath(path, AppContext.BaseDirectory), development);
        return new(directory, services.GetService<IScriptSandbox>());
    }

    public FeatureDirectory? Directory => _directory;

    public async Task<AppBundle?> Find(string owner, string name)
        => (await Bundles()).GetValueOrDefault((owner, name));

    public async Task<bool> IsOwner(string publisher)
        => (await Bundles()).Keys.Any(key => key.Owner == publisher);

    public async Task<IReadOnlyList<MarketplaceListing>> Listings()
        => [.. (await Bundles()).Select(entry => new MarketplaceListing(
            entry.Key.Owner, new AppSource.Shipped(), entry.Key.Name,
            entry.Value.Manifest.Title, entry.Value.Manifest.Description, entry.Value.Version, entry.Value.Manifest.Icon))];

    // Writes the bundle into the owner's folder and reloads, so Find and Listings switch to the new
    // contents together. A failed reload leaves the previous snapshot in place.
    public async Task<AppBundle> SaveToSource(string owner, AppBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (_directory is not { Writable: true } directory)
        { throw new InvalidOperationException($"{owner} has no writable features folder to save {bundle.Name} into."); }
        // Only an owner the folder already holds: an origin is brain state, not proof of a features owner.
        if (!await IsOwner(owner)) { throw new InvalidOperationException($"{owner} is not an owner in the features folder."); }
        directory.Write(owner, bundle);
        var reloaded = Load(directory, _sandbox);
        var bundles = await reloaded;
        lock (_gate) { _bundles = reloaded; }
        return bundles.GetValueOrDefault((owner, bundle.Name))
            ?? throw new InvalidOperationException($"{owner}/{bundle.Name} was written but did not load back.");
    }

    internal Task<IReadOnlyDictionary<(string Owner, string Name), AppBundle>> Bundles()
    {
        lock (_gate) { return _bundles ??= _directory is null ? Task.FromResult(Empty) : Load(_directory, _sandbox); }
    }

    private static readonly IReadOnlyDictionary<(string Owner, string Name), AppBundle> Empty
        = new Dictionary<(string Owner, string Name), AppBundle>();

    private static async Task<IReadOnlyDictionary<(string Owner, string Name), AppBundle>> Load(FeatureDirectory directory, IScriptSandbox? sandbox)
    {
        using var load = Telemetry.StartActivity("features.load");
        load?.SetTag("features.path", directory.Path);
        var bundles = new SortedDictionary<(string Owner, string Name), AppBundle>();
        foreach (var feature in directory.Load())
        {
            using var each = Telemetry.StartActivity("feature.load");
            each?.SetTag("feature.owner", feature.Owner);
            each?.SetTag("feature.name", feature.Bundle.Name);
            each?.SetTag("feature.version", feature.Bundle.Version);
            if (!bundles.TryAdd((feature.Owner, feature.Bundle.Name), feature.Bundle))
            { throw new InvalidOperationException($"{feature.Owner}/{feature.Bundle.Name} ships twice."); }
            await RequireCompiles(feature.Owner, feature.Bundle, sandbox);
        }
        load?.SetTag("features.count", bundles.Count);
        return bundles;
    }

    // The same gate AppNeuron.Publish applies; a host without a sandbox cannot check and does not.
    private static async Task RequireCompiles(string owner, AppBundle bundle, IScriptSandbox? sandbox)
    {
        var scripts = bundle.Files.Where(file => file.Key.EndsWith(".cs", StringComparison.Ordinal))
            .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
        if (scripts.Count == 0 || sandbox is null) { return; }
        scripts[AppFiles.DeclarationPath] = AppDeclaration.Write(new(bundle.Name, bundle.Version, bundle.Manifest, bundle.Spec));
        ScriptCompilationCheck check;
        try { check = await sandbox.Compile(scripts, CancellationToken.None); }
        catch (InvalidOperationException) { return; }
        if (check.Success) { return; }
        throw new InvalidOperationException($"{owner}/{bundle.Name} {bundle.Version} does not compile: "
            + string.Join("; ", check.Errors.Select(error => $"{error.File}:{error.Line} {error.Message}")));
    }
}
