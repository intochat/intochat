using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using System.Reflection;

namespace DigitalBrain.Capabilities.Connections;

// The neuron types of the hosted connection neurons (06-08): every [Neuron] class of the composed DigitalBrain modules that is a
// connection. Its type is the connector id its keys name. Each carries the capabilities its owning module's ModuleManifest provides (05-07).
public sealed class ConnectionNeuronTypes
{
    private readonly Dictionary<string, (Type Neuron, IReadOnlyList<CapabilityId> Provides)> _connectors = new(StringComparer.Ordinal);

    public ConnectionNeuronTypes(IEnumerable<Type> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        foreach (var group in modules.Distinct().GroupBy(static module => module.Assembly))
        {
            var assembly = group.Key;
            var selected = group.ToArray();
            var selectedSet = selected.ToHashSet();
            var allDeclaredModules = DeclaredModulesOf(assembly);
            foreach (var type in Loadable(assembly))
            {
                if (type is not { IsClass: true, IsAbstract: false } || !typeof(IConnection).IsAssignableFrom(type)) { continue; }
                if (type.GetCustomAttribute<NeuronAttribute>()?.Type is not { } connector) { continue; }

                var owners = OwningModules(type, allDeclaredModules);
                Type[] effectiveOwners;
                if (owners.Count > 0)
                {
                    effectiveOwners = [.. owners.Where(selectedSet.Contains)];
                    if (effectiveOwners.Length == 0) { continue; }
                }
                else
                {
                    effectiveOwners = selected;
                }

                var provides = effectiveOwners
                    .SelectMany(static owner => ModuleManifest.Of(owner).Provides)
                    .Distinct()
                    .ToArray();

                if (!_connectors.TryGetValue(connector, out var known) || (known.Provides.Count == 0 && provides.Length > 0))
                {
                    _connectors[connector] = (type, provides);
                }
            }
        }
        Types = _connectors.Keys.ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlySet<string> Types { get; }

    // The [Neuron] class serving connector, or null when none is hosted.
    public Type? NeuronOf(string connector) => _connectors.TryGetValue(connector, out var entry) ? entry.Neuron : null;

    // The capabilities the module of connector's neuron provides (its ModuleManifest.Provides); empty when none is hosted.
    public IReadOnlyList<CapabilityId> ProvidesOf(string connector)
        => _connectors.TryGetValue(connector, out var entry) ? entry.Provides : [];

    private static Type[] DeclaredModulesOf(Assembly assembly)
        => [.. Loadable(assembly).Where(static t => t is { IsClass: true, IsAbstract: false }
            && typeof(DigitalBrain.Modules.Module).IsAssignableFrom(t)
            && t.IsDefined(typeof(ModuleAttribute), inherit: false))];

    private static List<Type> OwningModules(Type type, Type[] declaredModules)
    {
        if (declaredModules.Length <= 1 || string.IsNullOrEmpty(type.Namespace)) { return []; }
        var ns = type.Namespace;
        var bestLength = -1;
        var best = new List<Type>();
        foreach (var module in declaredModules)
        {
            var modNs = module.Namespace;
            if (string.IsNullOrEmpty(modNs)) { continue; }
            var matches = string.Equals(ns, modNs, StringComparison.Ordinal)
                || (ns.Length > modNs.Length && ns[modNs.Length] == '.' && ns.StartsWith(modNs, StringComparison.Ordinal));
            if (!matches) { continue; }
            if (modNs.Length > bestLength)
            {
                bestLength = modNs.Length;
                best.Clear();
                best.Add(module);
            }
            else if (modNs.Length == bestLength)
            {
                best.Add(module);
            }
        }
        return best;
    }

    private static IEnumerable<Type> Loadable(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error) { return error.Types.OfType<Type>(); }
    }
}
