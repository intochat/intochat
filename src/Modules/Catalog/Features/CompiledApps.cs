using System.Reflection;
using DigitalBrain.Components;

namespace IntoChat.OS.Modules.Apps;

// Compiled apps in a module assembly: ComponentManifest ([Screen]) is always the base for any [Neuron("app.<name>")]
// screen class, with any public static AppBundle Bundle overlaying only non-attribute extras (operations, settings,
// toolbar, studio, side width, description). Standalone Bundles without a screen class (such as files) remain supported.
internal static class CompiledApps
{
    internal readonly record struct Compiled(string Owner, AppBundle Bundle, FeatureOrigin.ModuleSource Origin, Type AppType);

    internal static IEnumerable<Compiled> Of(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error) { types = [.. error.Types.Where(type => type is not null)!]; }

        var screenTypes = new Dictionary<string, Type>(StringComparer.Ordinal);
        var bundlesByName = new Dictionary<string, (AppBundle Bundle, Type DeclaringType)>(StringComparer.Ordinal);

        foreach (var type in types.OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            if (type is { IsClass: true, IsAbstract: false } && !type.ContainsGenericParameters
                && type.GetCustomAttribute<DigitalBrain.Neurons.NeuronAttribute>(false) is { } neuronAttr
                && neuronAttr.Type.StartsWith("app.", StringComparison.Ordinal))
            {
                screenTypes.TryAdd(neuronAttr.Type["app.".Length..], type);
            }

            var property = type.GetProperty("Bundle", BindingFlags.Public | BindingFlags.Static);
            if (property is { PropertyType: var kind, GetMethod: not null } && kind == typeof(AppBundle)
                && property.GetValue(null) is AppBundle bundle && bundle.Name.Length > 0)
            {
                bundlesByName.TryAdd(bundle.Name, (bundle, type));
            }
        }

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var origin = new FeatureOrigin.ModuleSource(assembly);

        foreach (var (name, type) in screenTypes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var component = ComponentManifests.Of(type) ?? ComponentManifests.Of(new ComponentId("app." + name));
            var hasBundle = bundlesByName.TryGetValue(name, out var declared);
            if (component is null && !hasBundle) { continue; }

            var resolved = component is not null
                ? MergeWithBase(name, type, component, hasBundle ? declared.Bundle : null)
                : declared.Bundle with { CompiledType = declared.Bundle.CompiledType ?? type.AssemblyQualifiedName };

            emitted.Add(name);
            yield return new(ShippedApps.Publisher, resolved, origin, type);
        }

        foreach (var (name, (bundle, declaringType)) in bundlesByName.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (emitted.Contains(name)) { continue; }
            emitted.Add(name);
            yield return new(ShippedApps.Publisher, bundle, origin, declaringType);
        }
    }

    private static AppBundle MergeWithBase(string name, Type appType, ComponentManifest component, AppBundle? overlay)
    {
        var baseManifest = AppManifest.From(component);
        if (string.IsNullOrWhiteSpace(baseManifest.Title))
        {
            baseManifest = baseManifest with { Title = name };
        }

        if (overlay is null)
        {
            return new AppBundle(
                Name: name,
                Version: "1.0.0",
                Manifest: baseManifest,
                Spec: string.Empty,
                Files: new Dictionary<string, string>(StringComparer.Ordinal),
                CompiledType: appType.AssemblyQualifiedName);
        }

        var extra = overlay.Manifest;
        // ComponentManifest ([Screen]) owns Title, Icon, Category, Dock, and Launch; Bundle only overlays non-face extras.
        var merged = baseManifest with
        {
            Description = string.IsNullOrWhiteSpace(baseManifest.Description) ? extra.Description : baseManifest.Description,
            Operations = extra.Operations.Length > 0 ? extra.Operations : baseManifest.Operations,
            Settings = extra.Settings.Length > 0 ? extra.Settings : baseManifest.Settings,
            Slots = extra.Slots.Length > 0 ? extra.Slots : baseManifest.Slots,
            Toolbar = extra.Toolbar ?? baseManifest.Toolbar,
            Requires = extra.Requires ?? baseManifest.Requires,
            Headless = extra.Headless || baseManifest.Headless,
            SideWidth = extra.SideWidth ?? baseManifest.SideWidth,
            Studio = extra.Studio || baseManifest.Studio,
        };

        return overlay with
        {
            Manifest = merged,
            CompiledType = overlay.CompiledType ?? appType.AssemblyQualifiedName,
        };
    }
}
