using System.ComponentModel;
using System.Reflection;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Contracts.Configuration;

namespace IntoChat.OS.Framework;

// The kernel's compiled type layer: every neuron contract, signal and connector the composed modules
// bring, built once per host. Kernel grains are not neurons, so they never enter it. A brain client has no layer of its own: it asks the kernel over the wire.
public sealed class CompiledTypes
{
    private readonly Dictionary<string, Type> _neurons;
    private TypeEntry[]? _addressed;

    // The module type sources add what a module declares beyond its contract assemblies (the Connections assembly
    // adds its connectors); the kernel itself knows no connector shape.
    public CompiledTypes(ModuleInventory modules, IEnumerable<IModuleTypeSource> sources)
        : this(modules.ContractAssemblies(), assembly => modules.Declaring(assembly)?.Id.ToString() ?? assembly.GetName().Name!, modules.Modules, sources) { }

    // Without a module composition an assembly names itself and no connectors are declared.
    public CompiledTypes(IEnumerable<Assembly> assemblies) : this(assemblies, assembly => assembly.GetName().Name!, [], []) { }

    private CompiledTypes(IEnumerable<Assembly> assemblies, Func<Assembly, string> provider, IReadOnlyList<IntoChat.OS.Framework.Modules.Module> modules,
        IEnumerable<IModuleTypeSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var typeSources = sources.ToArray();
        // The host's confirmation inbox exists in every composition. Its framework signals are part of that public surface,
        // without exposing unrelated runtime contracts or every type loaded in the host.
        var visible = assemblies.Distinct().SelectMany(assembly => assembly.GetExportedTypes())
            .Concat([typeof(DigitalBrain.Confirmation.ConfirmationRequested), typeof(DigitalBrain.Confirmation.ConfirmationResolved)])
            .Distinct().ToArray();
        _neurons = visible.Where(IsNeuron).ToDictionary(type => type.FullName!, StringComparer.Ordinal);
        var aliases = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var contract in _neurons.Values)
        {
            var alias = NeuronAliases.Of(contract);
            if (string.IsNullOrWhiteSpace(alias)) { throw new InvalidOperationException($"Public neuron contract {contract.FullName} requires a [NeuronAlias] or [Alias]."); }
            if (!aliases.TryAdd(alias, contract)) { throw new InvalidOperationException($"Duplicate neuron alias '{alias}'."); }
        }
        Entries =
        [
            .. aliases.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => Neuron(pair.Value, pair.Key, provider(pair.Value.Assembly))),
            .. visible.Where(IsSignal).OrderBy(type => type.FullName, StringComparer.Ordinal).Select(signal => Signal(signal, provider(signal.Assembly))),
            .. modules.SelectMany(module => typeSources.SelectMany(source => source.TypesOf(module))),
        ];
        Assemblies = [.. new[] { typeof(Signal).Assembly }.Concat(_neurons.Values.Select(type => type.Assembly)).Distinct()];
    }

    // Unaddressed: a neuron's Alias is its contract alias. The kernel serves Addressed.
    public TypeEntry[] Entries { get; }

    // The vocabulary and every assembly a compiled neuron contract lives in: what a script must carry.
    public Assembly[] Assemblies { get; }

    public Type Contract(string name) => _neurons.TryGetValue(name, out var contract)
        ? contract
        : throw new ArgumentException($"{name} is not a neuron contract of this brain.");

    // A neuron's Alias becomes the neuron type its contract resolves to, as instances record it; a contract several neurons
    // implement keeps its contract alias. A plain contract names its [Neuron] type; a connection contract (06-08) names none.
    // Any other contract is a still-grain's, whose type only the kernel composition (the grain factory's owner) resolves:
    // stillGrainType answers it, or null.
    public TypeEntry[] Addressed(Func<Type, string?> stillGrainType)
    {
        ArgumentNullException.ThrowIfNull(stillGrainType);
        return _addressed ??= [.. Entries.Select(entry => entry.Kind == TypeKind.Neuron && TypeOf(stillGrainType, _neurons[entry.Name]) is { } type ? entry with { Alias = type } : entry)];
    }

    private static string? TypeOf(Func<Type, string?> stillGrainType, Type contract)
        => IntoChat.OS.Runtime.PlainNeuronContracts.Is(contract) ? IntoChat.OS.Runtime.PlainNeuronContracts.TypeOf(contract) : stillGrainType(contract);

    // A legacy grain contract, or a migrated plain contract that names its [Neuron] type (06-03). IRetainsSignals is the legacy
    // retention side-interface every retaining grain adds, never a neuron of its own: once one grain is left implementing it
    // (06-08), Orleans resolves it to that grain's type and it would shadow the grain's own contract in listings.
    // A plain contract marked [Internal] as a whole (the connection index, 06-08) is module plumbing no edge caller reaches, so no
    // script compiles against it either.
    private static bool IsNeuron(Type type)
        => (type.IsInterface && type != typeof(INeuron) && type != typeof(IRetainsSignals) && typeof(INeuron).IsAssignableFrom(type))
            || (IntoChat.OS.Runtime.PlainNeuronContracts.Is(type) && !type.IsDefined(typeof(DigitalBrain.Access.InternalAttribute), inherit: false));

    // An os signal, or a DigitalBrain [Signal] record a migrated neuron publishes (06-03).
    private static bool IsSignal(Type type)
        => !type.IsAbstract && ((typeof(Signal).IsAssignableFrom(type) && type != typeof(Signal))
            || (typeof(DigitalBrain.Signal).IsAssignableFrom(type) && type.IsDefined(typeof(DigitalBrain.SignalAttribute), inherit: false)));

    private static TypeEntry Neuron(Type contract, string alias, string provider)
    {
        var name = contract.Name.StartsWith('I') ? contract.Name[1..] : contract.Name;
        string[] methods = [.. new[] { contract }.Concat(contract.GetInterfaces())
            .Where(type => !type.IsAssignableFrom(typeof(INeuron)))
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => !method.IsSpecialName)
            .Select(method => method + (method.GetCustomAttribute<DescriptionAttribute>() is { } description ? " — " + description.Description : ""))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        return new(contract.FullName!, alias, TypeKind.Neuron, provider,
            contract.GetCustomAttribute<DescriptionAttribute>()?.Description ?? name, methods,
            [.. contract.GetInterfaces().Where(IsNeuron).Select(type => type.FullName!).Order(StringComparer.Ordinal)]);
    }

    private static TypeEntry Signal(Type signal, string provider)
        => new(signal.FullName!, NeuronAliases.Of(signal) ?? signal.GetCustomAttribute<DigitalBrain.SignalAttribute>(inherit: false)?.Alias ?? signal.FullName!, TypeKind.Signal, provider,
            signal.GetCustomAttribute<DescriptionAttribute>()?.Description ?? signal.Name,
            [.. signal.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.DeclaringType != typeof(Signal))
                .Select(property => TypeName(property.PropertyType) + " " + property.Name)], []);

    private static string TypeName(Type type) => type.IsGenericType
        ? type.Name.Split('`')[0] + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">"
        : type.Name;
}
