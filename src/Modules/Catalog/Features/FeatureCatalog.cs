using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps;

// The apps the host ships: every compiled [App] type in the registered module assemblies. Code, not content
// anyone wrote at runtime, so it is read once; an app two assemblies both ship is skipped and reported through
// Failures, and every other app still loads.
public sealed class FeatureCatalog
{
    internal static readonly ActivitySource Telemetry = new("IntoChat.OS.Modules.Apps.Features");

    private readonly Assembly[] _modules;
    private readonly Lazy<Loaded> _loaded;

    internal FeatureCatalog(Assembly? modules)
        : this(modules is null ? [] : [modules]) { }

    public FeatureCatalog(IEnumerable<Assembly> modules)
    {
        _modules = [.. (modules ?? []).Where(a => a is not null).Distinct()];
        _loaded = new(Load);
    }

    internal static FeatureCatalog From(IServiceProvider services)
    {
        var registered = services.GetServices<CompiledAppsRegistration>().Select(r => r.Assembly).ToList();
        if (!registered.Contains(typeof(AppsModule).Assembly))
        {
            registered.Insert(0, typeof(AppsModule).Assembly);
        }
        return new(registered);
    }

    public Task<IReadOnlyList<FeatureFailure>> Failures() => Task.FromResult<IReadOnlyList<FeatureFailure>>(_loaded.Value.Failures);

    public async Task<AppBundle?> Find(string owner, string name)
        => (await Bundles()).GetValueOrDefault((owner, name))
            ?? (string.Equals(owner, ShippedPackages.Publisher, StringComparison.OrdinalIgnoreCase)
                ? ShippedPackages.Find(name)?.ToAppBundle()
                : null);

    public async Task<bool> IsOwner(string publisher)
        => string.Equals(publisher, ShippedPackages.Publisher, StringComparison.OrdinalIgnoreCase)
            || (await Bundles()).Keys.Any(key => key.Owner == publisher);

    public async Task<IReadOnlyList<MarketplaceListing>> Listings()
        => [.. (await Bundles()).Select(entry => new MarketplaceListing(
            entry.Key.Owner, new AppSource.Shipped(), entry.Key.Name,
            entry.Value.Manifest.Title, entry.Value.Manifest.Description, entry.Value.Version, entry.Value.Manifest.Icon, entry.Value.Manifest.Requires))];

    public Task<FeatureOrigin?> Origin(string owner, string name)
        => Task.FromResult(_loaded.Value.Origins.GetValueOrDefault((owner, name)));

    // Every shipped os app, plus every ShippedApps root.
    public Task<IReadOnlyList<(string Publisher, string Name)>> BootSet()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (owner, name) in _loaded.Value.Origins.Keys)
        {
            if (owner == ShippedApps.Publisher) { names.Add(name); }
        }
        foreach (var root in ShippedApps.Roots) { names.Add(root); }
        return Task.FromResult<IReadOnlyList<(string Publisher, string Name)>>([.. names.Select(name => (ShippedApps.Publisher, name))]);
    }

    // A compiled app changes only with the host's build, so reading it again is reading it.
    public Task<AppBundle?> Reload(string owner, string name) => Find(owner, name);

    internal Task<IReadOnlyDictionary<(string Owner, string Name), AppBundle>> Bundles() => Task.FromResult(_loaded.Value.Bundles);

    private Loaded Load()
    {
        using var load = Telemetry.StartActivity("features.load");
        var bundles = new SortedDictionary<(string Owner, string Name), AppBundle>();
        var origins = new Dictionary<(string Owner, string Name), FeatureOrigin>();
        var twice = new HashSet<(string Owner, string Name)>();
        List<FeatureFailure> failures = [];
        foreach (var moduleAssembly in _modules)
        {
            foreach (var compiled in CompiledApps.Of(moduleAssembly))
            {
                var key = (compiled.Owner, compiled.Bundle.Name);
                if (twice.Contains(key)) { continue; }
                if (bundles.ContainsKey(key))
                {
                    var reason = $"{compiled.Owner}/{compiled.Bundle.Name} ships twice: {((FeatureOrigin.Module)origins[key]).Assembly.GetName().Name} and {compiled.Origin.Assembly.GetName().Name}.";
                    failures.Add(new(compiled.Owner, compiled.Bundle.Name, compiled.Bundle.Version, reason));
                    bundles.Remove(key);
                    origins.Remove(key);
                    twice.Add(key);
                    continue;
                }
                bundles[key] = compiled.Bundle;
                origins[key] = compiled.Origin;
            }
        }
        load?.SetTag("features.count", bundles.Count);
        load?.SetTag("features.failed", failures.Count);
        return new(bundles, [.. failures], origins);
    }

    private sealed record Loaded(
        IReadOnlyDictionary<(string Owner, string Name), AppBundle> Bundles,
        FeatureFailure[] Failures,
        IReadOnlyDictionary<(string Owner, string Name), FeatureOrigin> Origins);
}

public sealed record FeatureFailure(string Owner, string Name, string Version, string Reason);
