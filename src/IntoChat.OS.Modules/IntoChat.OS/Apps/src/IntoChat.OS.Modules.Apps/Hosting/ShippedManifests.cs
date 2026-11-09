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

    public static double SideWidth(AppManifest manifest) => manifest.SideWidth ?? DefaultSideWidth;
}
