using System.Reflection;

namespace IntoChat.OS.Modules.Apps;

// Registers an assembly whose compiled [App] types ship in FeatureCatalog. Boot names the app types the host assigns to every brain
// at start (ino and settings are the shell's); the host finds their bundles here, so it names no app.
public sealed record CompiledAppsRegistration(Assembly Assembly, Type[]? Boot = null);

// Compiled apps in a module assembly: each public static AppBundle Bundle shipped by the module, paired with
// its [Neuron("app.<name>")] screen class when compiled as a screen neuron (or its declaring bundle owner type).
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
        foreach (var type in types.OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            if (type is not { IsClass: true, IsAbstract: false } || type.ContainsGenericParameters) { continue; }
            if (type.GetCustomAttribute<DigitalBrain.Neurons.NeuronAttribute>(false) is { } neuronAttr
                && neuronAttr.Type.StartsWith("app.", StringComparison.Ordinal))
            {
                screenTypes.TryAdd(neuronAttr.Type["app.".Length..], type);
            }
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in types.OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            var property = type.GetProperty("Bundle", BindingFlags.Public | BindingFlags.Static);
            if (property is not { PropertyType: var kind, GetMethod: not null } || kind != typeof(AppBundle)) { continue; }
            if (property.GetValue(null) is not AppBundle bundle || bundle.Name.Length == 0 || !seen.Add(bundle.Name)) { continue; }
            var appType = screenTypes.GetValueOrDefault(bundle.Name, type);
            yield return new(ShippedApps.Publisher, bundle, new FeatureOrigin.ModuleSource(assembly), appType);
        }
    }
}
