using System.Reflection;
using DigitalBrain.Components;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

// Registers an assembly whose compiled [App] types ship in FeatureCatalog. Boot names the app types the host assigns to every brain
// at start (ino and settings are the shell's); the host finds their bundles here, so it names no app.
public sealed record CompiledAppsRegistration(Assembly Assembly, Type[]? Boot = null);

// Compiled apps in a module assembly: each public static AppBundle Bundle shipped by the module (winning where present),
// plus any [Neuron("app.<name>")] screen class with a generated ComponentManifest, paired with its screen class.
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
        foreach (var (name, type) in screenTypes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (seen.Contains(name)) { continue; }
            var manifest = ComponentManifests.Of(type) ?? ComponentManifests.Of(new ComponentId("app." + name));
            if (manifest is null) { continue; }
            var bundle = BundleFromManifest(name, type, manifest);
            seen.Add(name);
            yield return new(ShippedApps.Publisher, bundle, new FeatureOrigin.ModuleSource(assembly), type);
        }
    }

    private static AppBundle BundleFromManifest(string name, Type appType, ComponentManifest manifest)
    {
        var title = !string.IsNullOrWhiteSpace(manifest.Face?.Title) ? manifest.Face!.Title : name;
        var description = manifest.Face?.Description ?? string.Empty;
        var operations = manifest.Operations
            .Select(static op => new AppOperation(op.Name, op.Description))
            .ToArray();
        var slots = manifest.Slots
            .Select(static slot => new AppSlot(slot.Name, ContractIdOf(slot.Contract)))
            .ToArray();
        var appManifest = new AppManifest(
            Title: title,
            Description: description,
            Operations: operations,
            Settings: [],
            Slots: slots,
            Icon: manifest.Face?.Icon,
            Category: manifest.Face?.Category);
        return new AppBundle(
            Name: name,
            Version: "1.0.0",
            Manifest: appManifest,
            Spec: string.Empty,
            Files: new Dictionary<string, string>(StringComparer.Ordinal),
            CompiledType: appType.AssemblyQualifiedName);
    }

    private static string ContractIdOf(Type contract)
        => contract.GetCustomAttribute<DefaultNeuronTypeAttribute>(inherit: false)?.Type
            ?? contract.GetCustomAttribute<NeuronAliasAttribute>(inherit: false)?.Alias
            ?? contract.Name;
}
