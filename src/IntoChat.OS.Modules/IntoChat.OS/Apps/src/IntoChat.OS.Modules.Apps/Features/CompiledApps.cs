using System.Reflection;

namespace IntoChat.OS.Modules.Apps;

// Registers an assembly whose compiled [App] types ship in FeatureCatalog.
public sealed record CompiledAppsRegistration(Assembly Assembly);

// Compiled apps in a module assembly: each [App] whose bundle is a public static AppBundle Bundle
// on a type in that assembly. The script folder does not carry them.
internal static class CompiledApps
{
    internal readonly record struct Compiled(string Owner, AppBundle Bundle, FeatureOrigin.Module Origin);

    internal static IEnumerable<Compiled> Of(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error) { types = [.. error.Types.Where(type => type is not null)!]; }
        var bundles = new Dictionary<string, AppBundle>(StringComparer.Ordinal);
        foreach (var type in types.OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            var property = type.GetProperty("Bundle", BindingFlags.Public | BindingFlags.Static);
            if (property is not { PropertyType: var kind, GetMethod: not null } || kind != typeof(AppBundle)) { continue; }
            if (property.GetValue(null) is AppBundle bundle && bundle.Name.Length > 0) { bundles.TryAdd(bundle.Name, bundle); }
        }
        foreach (var type in types.OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            if (type is not { IsClass: true, IsAbstract: false } || type.ContainsGenericParameters) { continue; }
            var id = type.GetCustomAttribute<DigitalBrain.AppAttribute>(false)?.Id;
            if (id is null || !bundles.TryGetValue(id, out var bundle)) { continue; }
            yield return new(ShippedApps.Publisher, bundle, new FeatureOrigin.Module(assembly));
        }
    }
}
