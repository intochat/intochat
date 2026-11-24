using DigitalBrain.Components;

namespace IntoChat.OS.Modules.Apps;

// The share format: a published version's content. No connections, settings as defaults only.
public sealed record AppBundle(
    string Name,
    string Version,
    AppManifest Manifest,
    string Spec,
    Dictionary<string, string> Files,
    string? CompiledType = null)
{
    /// <summary>
    /// Builds a compiled app bundle whose display identity (<c>Title</c>, <c>Icon</c>, <c>Category</c>,
    /// <c>Dock</c>, <c>Launch</c>) comes solely from <typeparamref name="TApp"/>'s generated
    /// <see cref="ComponentManifest"/> (<c>[Screen]</c>), overlaying only extras that <c>[Screen]</c> does not express.
    /// </summary>
    public static AppBundle FromScreen<TApp>(
        string name,
        string version,
        string? description = null,
        AppOperation[]? operations = null,
        AppSetting[]? settings = null,
        AppSlot[]? slots = null,
        AppToolbarEntry[]? toolbar = null,
        AppRequirements? requires = null,
        bool headless = false,
        double? sideWidth = null,
        bool studio = false,
        string spec = "",
        Dictionary<string, string>? files = null)
    {
        var type = typeof(TApp);
        var component = ComponentManifests.Of(type) ?? ComponentManifests.Of(new ComponentId("app." + name));
        var baseManifest = component is not null
            ? AppManifest.From(component)
            : new AppManifest(name, string.Empty, [], [], []);
        var manifest = baseManifest with
        {
            Description = !string.IsNullOrWhiteSpace(description) ? description : baseManifest.Description,
            Operations = operations is { Length: > 0 } ? operations : baseManifest.Operations,
            Settings = settings is { Length: > 0 } ? settings : baseManifest.Settings,
            Slots = slots is { Length: > 0 } ? slots : baseManifest.Slots,
            Toolbar = toolbar ?? baseManifest.Toolbar,
            Requires = requires ?? baseManifest.Requires,
            Headless = headless || baseManifest.Headless,
            SideWidth = sideWidth ?? baseManifest.SideWidth,
            Studio = studio || baseManifest.Studio,
        };
        return new AppBundle(
            name,
            version,
            manifest,
            spec,
            files ?? new Dictionary<string, string>(StringComparer.Ordinal),
            type.AssemblyQualifiedName);
    }
}
