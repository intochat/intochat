using DigitalBrain.Components;
using DigitalBrain.Kernel;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.UI;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Runtime;

namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// Merges compiled apps (<see cref="FeatureCatalog"/> / <see cref="ComponentManifests"/>),
/// runtime <see cref="IApp"/> instances (concurrently, skipping compiled IDs), and deployed
/// <see cref="ISynapseStudio"/> micro-app units into a single ordered <see cref="AppTile"/> list.
/// Excludes <see cref="AppManifest.Headless"/> apps and <see cref="ShippedApps.Services"/>.
/// </summary>
[Neuron("apps.installed")]
internal sealed class InstalledAppsNeuron(
    FeatureCatalog? catalog = null,
    INeuronProxies? proxies = null,
    IBrainObserver? observer = null) : DigitalBrain.Neurons.Neuron, IInstalledApps
{
    public async Task<AppTile[]> List()
    {
        var scope = Id.Brain.Value;
        var caller = Context.Caller;
        if (caller is not null
            && caller.Actor is not DigitalBrain.Access.Actor.Kernel
            && !string.Equals(caller.Brain.Value, scope, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"Caller from brain '{caller.Brain.Value}' may not read apps.installed for brain '{scope}'.");
        }

        var tiles = new List<AppTile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Compiled apps from FeatureCatalog (covers apps with Bundles including standalone bundles like "files")
        if (catalog is not null)
        {
            var bundles = await catalog.Bundles();
            foreach (var ((owner, name), bundle) in bundles)
            {
                if (string.IsNullOrWhiteSpace(name) || ShippedApps.Services.Contains(name))
                {
                    continue;
                }

                if (bundle.Manifest.Headless)
                {
                    seen.Add(name);
                    continue;
                }

                var availability = await catalog.Availability(owner, name);
                if (availability is not AppAvailability.Ready)
                {
                    seen.Add(name);
                    continue;
                }

                if (!seen.Add(name))
                {
                    continue;
                }

                var manifest = bundle.Manifest;
                tiles.Add(new AppTile(
                    Id: name,
                    Title: string.IsNullOrWhiteSpace(manifest.Title) ? FormatTitle(name) : manifest.Title,
                    Icon: NormalizeIcon(manifest.Icon),
                    Category: manifest.Category,
                    Dock: manifest.Dock,
                    Launch: manifest.Launch,
                    Kind: "compiled",
                    Description: string.IsNullOrWhiteSpace(manifest.Description) ? null : manifest.Description));
            }
        }

        // 2. Compiled [Screen("app.*")] components not already in FeatureCatalog (e.g., bundle-less screens like customer-researcher)
        foreach (var component in ComponentManifests.All)
        {
            if (component.Face is null || !component.Id.Value.StartsWith("app.", StringComparison.Ordinal))
            {
                continue;
            }

            var id = component.Id.Value["app.".Length..];
            if (string.IsNullOrWhiteSpace(id) || ShippedApps.Services.Contains(id) || !seen.Add(id))
            {
                continue;
            }

            var face = component.Face;
            tiles.Add(new AppTile(
                Id: id,
                Title: string.IsNullOrWhiteSpace(face.Title) ? FormatTitle(id) : face.Title,
                Icon: NormalizeIcon(face.Icon),
                Category: face.Category,
                Dock: face.Dock,
                Launch: face.Maximized ? AppLaunch.Maximized : AppLaunch.Windowed,
                Kind: "compiled",
                Description: string.IsNullOrWhiteSpace(face.Description) ? null : face.Description));
        }

        // 3. Runtime IApp instances not already covered by compiled apps (read concurrently via Task.WhenAll)
        if (proxies is not null && observer is not null)
        {
            try
            {
                var runtimeApps = await InstalledApps.Read(proxies, observer, scope, CancellationToken.None, skipNames: seen);
                foreach (var (name, snapshot) in runtimeApps)
                {
                    if (string.IsNullOrWhiteSpace(name)
                        || ShippedApps.Services.Contains(name)
                        || snapshot.Manifest.Headless
                        || string.IsNullOrWhiteSpace(snapshot.Manifest.Title)
                        || !seen.Add(name))
                    {
                        continue;
                    }

                    var manifest = snapshot.Manifest;
                    tiles.Add(new AppTile(
                        Id: name,
                        Title: manifest.Title,
                        Icon: NormalizeIcon(manifest.Icon),
                        Category: string.IsNullOrWhiteSpace(manifest.Category) ? "Workspace" : manifest.Category,
                        Dock: manifest.Dock,
                        Launch: manifest.Launch,
                        Kind: "runtime",
                        Description: string.IsNullOrWhiteSpace(manifest.Description) ? null : manifest.Description));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                OsConsoleLog.For<InstalledAppsNeuron>().LogSwallowed(error, "Runtime IApp enumeration is optional when running standalone.");
            }
        }

        // 4. Deployed Synapse micro-app units from ISynapseStudio
        try
        {
            var studio = Context.Get<ISynapseStudio>(ISynapseStudio.Key(scope));
            var units = await studio.List();
            foreach (var unit in units)
            {
                var alias = unit.Aliases.FirstOrDefault() ?? unit.SourceKey;
                if (string.IsNullOrWhiteSpace(alias) || ShippedApps.Services.Contains(alias) || !seen.Add(alias))
                {
                    continue;
                }

                tiles.Add(new AppTile(
                    Id: alias,
                    Title: FormatTitle(alias),
                    Icon: "zap",
                    Category: "Micro-App",
                    Dock: null,
                    Launch: AppLaunch.Windowed,
                    Kind: "unit"));
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<InstalledAppsNeuron>().LogSwallowed(error, "SynapseStudio unit enumeration is optional when running standalone.");
        }

        return [.. tiles
            .OrderBy(static t => t.Dock ?? int.MaxValue)
            .ThenBy(static t => t.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static t => t.Id, StringComparer.Ordinal)];
    }

    private static string? NormalizeIcon(string? icon)
        => string.IsNullOrWhiteSpace(icon) || string.Equals(icon, "icon.svg", StringComparison.OrdinalIgnoreCase)
            ? null
            : icon;

    private static string FormatTitle(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) { return "App"; }
        var parts = id.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) { return id; }
        return string.Join(' ', parts.Select(static p => char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
