using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps;

// What the host shows of an app it ships comes from the app's own manifest: the title on a tab or dock button, the icon on a chat card,
// the width of its side panel. The host names no app to find them.
internal sealed class ShippedManifests(IReadOnlyDictionary<string, AppManifest> manifests)
{
    internal const double DefaultSideWidth = 236d;

    // A compiled app changes only with the host's build, so the manifests of a catalog are read once.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FeatureCatalog, ShippedManifests> Read = new();

    public static async Task<ShippedManifests> Of(IServiceProvider services)
        => services.GetService<FeatureCatalog>() is { } catalog ? await Of(catalog) : new(new Dictionary<string, AppManifest>());

    public static async Task<ShippedManifests> Of(FeatureCatalog catalog)
    {
        if (Read.TryGetValue(catalog, out var known)) { return known; }
        var bundles = await catalog.Bundles();
        var manifests = new ShippedManifests(bundles.Where(entry => entry.Key.Owner == ShippedApps.Publisher)
            .ToDictionary(entry => entry.Key.Name, entry => entry.Value.Manifest, StringComparer.Ordinal));
        return Read.GetValue(catalog, _ => manifests);
    }

    public string Title(string app)
        => manifests.TryGetValue(app, out var manifest) && !string.IsNullOrWhiteSpace(manifest.Title) ? manifest.Title
            : string.IsNullOrEmpty(app) ? "App" : char.ToUpperInvariant(app[0]) + app[1..];

    // A file name is the app's own glyph, not a named icon a chat card can show.
    public string Icon(string app)
        => manifests.TryGetValue(app, out var manifest) && !string.IsNullOrWhiteSpace(manifest.Icon) && !manifest.Icon.EndsWith(".svg", StringComparison.Ordinal)
            ? manifest.Icon : "grid";

    // The apps in the desktop's dock, in dock order.
    public IReadOnlyList<string> DockApps()
        => [.. manifests.Where(entry => entry.Value.Dock is not null).OrderBy(entry => entry.Value.Dock).ThenBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Key)];

    // The studios Ino hands routed prompts to, with what each one is for.
    public IReadOnlyList<(string App, string Description)> Studios()
        => [.. manifests.Where(entry => entry.Value.Studio).OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => (entry.Key, entry.Value.Description))];

    // The studio a `routed:<app>.<rest>` hint hands its prompt to: the dot is required (a bare `routed:photocraft` is not a route), the app must
    // be a studio, and it must declare an `edit` operation, which is what runs. Null otherwise.
    public string? StudioFor(string routeHint)
    {
        const string prefix = "routed:";
        if (!routeHint.StartsWith(prefix, StringComparison.Ordinal)) { return null; }
        var dot = routeHint.IndexOf('.', prefix.Length);
        if (dot < 0) { return null; }
        var app = routeHint[prefix.Length..dot];
        return manifests.TryGetValue(app, out var manifest) && manifest.Studio
            && manifest.Operations.Any(operation => operation.Name == StudioOperation) ? app : null;
    }

    internal const string StudioOperation = "edit";

    public static double SideWidth(AppManifest manifest) => manifest.SideWidth ?? DefaultSideWidth;
}
