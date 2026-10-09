using System.ComponentModel;
using System.Reflection;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Framework.Contracts;

public sealed class ContractCatalog
{
    private readonly Func<Assembly, string> _moduleId;
    private readonly Dictionary<string, Type> _neurons;
    public IReadOnlyList<Type> VisibleTypes { get; }

    public ContractCatalog(ModuleInventory modules)
        : this(modules.ContractAssemblies(), assembly => modules.Declaring(assembly)?.Id.ToString() ?? assembly.GetName().Name!) { }

    // Without a module composition (a brain client such as digitalbrain-mcp) an assembly names itself.
    public ContractCatalog(IEnumerable<Assembly> assemblies) : this(assemblies, assembly => assembly.GetName().Name!) { }

    private ContractCatalog(IEnumerable<Assembly> assemblies, Func<Assembly, string> moduleId)
    {
        _moduleId = moduleId;
        VisibleTypes = Array.AsReadOnly(assemblies.Distinct()
            .SelectMany(assembly => assembly.GetExportedTypes()).Where(type => !PlatformOnlyAttribute.AppliesTo(type)).ToArray());
        _neurons = VisibleTypes.Where(IsNeuron).ToDictionary(type => type.FullName!, StringComparer.Ordinal);
        Signals = Array.AsReadOnly(VisibleTypes.Where(IsSignal)
            .Select(signal => new SignalType(moduleId(signal.Assembly), Describe(signal)))
            .OrderBy(signal => signal.Shape, StringComparer.Ordinal).ToArray());
    }

    // A brain client's addressable surface is whatever contract assemblies are deployed beside it.
    public static Assembly[] Deployed() =>
    [
        typeof(INeuron).Assembly,
        typeof(DigitalBrain.Integrations.IConnectionRequests).Assembly,
        .. Directory.GetFiles(AppContext.BaseDirectory, "DigitalBrain.Modules.*.Contracts.dll")
            .Select(path => Assembly.Load(AssemblyName.GetAssemblyName(path))),
    ];

    public IReadOnlyCollection<string> Neurons => _neurons.Keys;

    public IReadOnlyList<SignalType> Signals { get; }

    public Type Find(string name) => _neurons.TryGetValue(name, out var contract)
        ? contract
        : throw new ArgumentException($"{name} is not a loaded neuron contract. Pass the full type name from neurons_list.");

    public static bool IsNeuron(Type type) => type.IsInterface && type != typeof(INeuron) && typeof(INeuron).IsAssignableFrom(type);

    public static bool IsSignal(Type type) => typeof(Signal).IsAssignableFrom(type) && type != typeof(Signal) && !type.IsAbstract;

    private readonly Lock _gate = new();
    private IReadOnlyList<NeuronType>? _types;

    public IReadOnlyList<NeuronType> Read()
    {
        lock (_gate)
        {
            if (_types is not null) { return _types; }
            var types = new Dictionary<string, (Type Contract, NeuronType Metadata)>(StringComparer.Ordinal);
            foreach (var contract in _neurons.Values)
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
                types.Add(id, (contract, new(id, _moduleId(contract.Assembly), name,
                    contract.GetCustomAttribute<DescriptionAttribute>()?.Description ?? name, Array.AsReadOnly(methods),
                    contract.FullName!)));
            }
            _types = Array.AsReadOnly(types.Values.Select(type => type.Metadata).OrderBy(type => type.Id, StringComparer.Ordinal).ToArray());
            return _types;
        }
    }

    // The router's menu, contract half: contracts marked [Routable] and nothing else. Keyed runtime
    // tool entries are concrete instances the router adds on their own; this is the types-only set.
    public IReadOnlyList<NeuronType> Routable()
    {
        lock (_gate)
        {
            return Array.AsReadOnly(Read().Where(type => IsRoutable(type.Contract)).ToArray());
        }
    }

    public bool IsRoutable(string contractFullName)
        => _neurons.TryGetValue(contractFullName, out var contract) && RoutableAttribute.AppliesTo(contract);

    private static string Describe(Type signal)
        => signal.FullName + " { " + string.Join("; ", signal
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.DeclaringType != typeof(Signal))
            .Select(property => TypeName(property.PropertyType) + " " + property.Name)) + " }";

    private static string TypeName(Type type) => type.IsGenericType
        ? type.Name.Split('`')[0] + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">"
        : type.Name;
}
