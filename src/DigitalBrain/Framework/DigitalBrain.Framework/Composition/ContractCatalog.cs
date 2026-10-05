using System.ComponentModel;
using System.Reflection;
using DigitalBrain;

namespace DigitalBrain.Framework;

public sealed class ContractCatalog
{
    private readonly IReadOnlyDictionary<Assembly, string> _moduleIds;
    public IReadOnlyList<Type> VisibleTypes { get; }

    public ContractCatalog(ModuleInventory modules) : this(modules.ContractAssemblies(), modules.Types
        .SelectMany(module => ModuleInventory.ContractAssembliesOf(module.Assembly).Select(assembly => (assembly, Id: NeuronType.ModuleIdOf(module))))
        .GroupBy(item => item.assembly).ToDictionary(group => group.Key, group => group.First().Id))
    { }

    public ContractCatalog(IEnumerable<Assembly> assemblies) : this(assemblies, new Dictionary<Assembly, string>()) { }

    private ContractCatalog(IEnumerable<Assembly> assemblies, IReadOnlyDictionary<Assembly, string> moduleIds)
    {
        _moduleIds = moduleIds;
        VisibleTypes = Array.AsReadOnly(assemblies.Distinct().Where(assembly => !PlatformAssemblyAttribute.IsPlatform(assembly))
            .SelectMany(assembly => assembly.GetExportedTypes()).Where(type => !PlatformOnlyAttribute.AppliesTo(type)).ToArray());
    }

    public static bool IsNeuron(Type type) => type.IsInterface && type != typeof(INeuron) && typeof(INeuron).IsAssignableFrom(type);

    private readonly Lock _gate = new();
    private IReadOnlyList<NeuronType>? _types;

    public IReadOnlyList<NeuronType> Read()
    {
        lock (_gate)
        {
            if (_types is not null) { return _types; }
            var types = new Dictionary<string, (Type Contract, NeuronType Metadata)>(StringComparer.Ordinal);
            foreach (var contracts in VisibleTypes.GroupBy(type => type.Assembly))
            {
                var signals = contracts.Where(type => typeof(Signal).IsAssignableFrom(type) && type != typeof(Signal) && !type.IsAbstract).ToArray();
                foreach (var contract in contracts.Where(IsNeuron))
                {
                    var id = contract.GetCustomAttribute<AliasAttribute>()?.Alias;
                    if (string.IsNullOrWhiteSpace(id)) { throw new InvalidOperationException($"Public neuron contract {contract.FullName} requires an Orleans alias."); }
                    if (types.TryGetValue(id, out var previous))
                    {
                        if (previous.Contract != contract) { throw new InvalidOperationException($"Duplicate neuron alias '{id}'."); }
                        continue;
                    }
                    var name = contract.Name.StartsWith('I') ? contract.Name[1..] : contract.Name;
                    var methods = new[] { contract }.Concat(contract.GetInterfaces())
                        .Where(type => !type.IsAssignableFrom(typeof(INeuron)))
                        .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                        .Where(method => !method.IsSpecialName)
                        .Select(method => method + (method.GetCustomAttribute<DescriptionAttribute>() is { } description ? " — " + description.Description : ""))
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                    types.Add(id, (contract, new(id, ModuleId(contract.Assembly), name,
                        contract.GetCustomAttribute<DescriptionAttribute>()?.Description ?? name, Array.AsReadOnly(methods),
                        contract.FullName!, Array.AsReadOnly(SignalsNear(contract, signals)))));
                }
            }
            _types = Array.AsReadOnly(types.Values.Select(type => type.Metadata).OrderBy(type => type.Id, StringComparer.Ordinal).ToArray());
            return _types;
        }
    }

    private string ModuleId(Assembly assembly) => _moduleIds.TryGetValue(assembly, out var id) ? id
        : assembly.GetName().Name!.Replace("DigitalBrain.Modules.", "").Replace(".Contracts", "").Split('.').Last().ToLowerInvariant();

    // Signals live beside their neuron contract ("...Timers.ITimer" with "...Timers.Signals.TimerTick"),
    // so a contract carries the signals of its own namespace and its ".Signals" sub-namespace.
    private static string[] SignalsNear(Type contract, Type[] signals) => [.. signals
        .Where(signal => signal.Namespace == contract.Namespace || signal.Namespace == contract.Namespace + ".Signals")
        .Select(Describe)
        .Order(StringComparer.Ordinal)];

    private static string Describe(Type signal)
        => signal.FullName + " { " + string.Join("; ", signal
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.DeclaringType != typeof(Signal))
            .Select(property => TypeName(property.PropertyType) + " " + property.Name)) + " }";

    private static string TypeName(Type type) => type.IsGenericType
        ? type.Name.Split('`')[0] + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">"
        : type.Name;
}
