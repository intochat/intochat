using System.ComponentModel;
using System.Reflection;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Contracts;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Modules;

namespace IntoChat.OS.Host.Kernel;

// The kernel's compiled type layer: every neuron contract, screen neuron, signal and connector the composed modules
// bring, built once per host. Kernel grains are not neurons, so they never enter it. A brain client has no layer of its own: it asks the kernel over the wire.
public sealed class CompiledTypes
{
    private readonly Dictionary<string, Type> _neurons;
    private readonly HashSet<string> _screenTypes;
    private BrainType[]? _addressed;

    // The module type sources add what a module declares beyond its contract assemblies (the Connections assembly
    // adds its connectors); the kernel itself knows no connector shape.
    public CompiledTypes(ModuleInventory modules, IEnumerable<IModuleTypeSource> sources, IEnumerable<Assembly>? screenAssemblies = null)
        : this(
            modules.ContractAssemblies(),
            screenAssemblies ?? [],
            assembly => modules.Declaring(assembly)?.Id.ToString() ?? assembly.GetName().Name!,
            modules.Modules,
            sources) { }

    // Without a module composition an assembly names itself and no connectors are declared.
    public CompiledTypes(IEnumerable<Assembly> assemblies, IEnumerable<Assembly>? screenAssemblies = null)
        : this(assemblies, screenAssemblies ?? [], assembly => assembly.GetName().Name!, [], []) { }

    private CompiledTypes(
        IEnumerable<Assembly> assemblies,
        IEnumerable<Assembly> screenAssemblies,
        Func<Assembly, string> provider,
        IReadOnlyList<IntoChat.OS.Host.Modules.OsModule> modules,
        IEnumerable<IModuleTypeSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var contractAssemblies = assemblies.Distinct().ToArray();
        var typeSources = sources.ToArray();
        // The host's confirmation inbox exists in every composition. Its framework signals are part of that public surface,
        // without exposing unrelated runtime contracts or every type loaded in the host.
        var visible = contractAssemblies.SelectMany(assembly => assembly.GetExportedTypes())
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

        // Screen neurons (e.g. app.gallery, app.files, app.aspire) implement IScreen with a [Neuron] type and no per-app interface.
        // Registering them against IScreen lets neurons_list and neurons_call share one truth.
        var screens = new Dictionary<string, (Type ScreenClass, string Provider)>(StringComparer.Ordinal);
        foreach (var assembly in contractAssemblies.Concat(screenAssemblies).Distinct())
        {
            foreach (var type in assembly.GetExportedTypes())
            {
                if (!IsScreenNeuron(type, out var screenType)) { continue; }
                if (aliases.ContainsKey(screenType)) { continue; }
                if (screens.TryAdd(screenType, (type, provider(assembly))))
                {
                    _neurons.TryAdd(screenType, typeof(IScreen));
                }
            }
        }
        _screenTypes = new HashSet<string>(screens.Keys, StringComparer.Ordinal);

        Entries =
        [
            .. aliases.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => Neuron(pair.Value, pair.Key, provider(pair.Value.Assembly))),
            .. screens.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => ScreenNeuron(pair.Value.ScreenClass, pair.Key, pair.Value.Provider)),
            .. visible.Where(IsSignal).OrderBy(type => type.FullName, StringComparer.Ordinal).Select(signal => Signal(signal, provider(signal.Assembly))),
            .. modules.SelectMany(module => typeSources.SelectMany(source => source.TypesOf(module))),
        ];
        Assemblies = [.. new[] { typeof(Signal).Assembly, typeof(IBrain).Assembly }.Concat(_neurons.Values.Select(type => type.Assembly)).Distinct()];
    }

    // Unaddressed: a neuron's Alias is its contract alias. The kernel serves Addressed.
    public BrainType[] Entries { get; }

    // The vocabulary and every assembly a compiled neuron contract lives in: what a script must carry.
    public Assembly[] Assemblies { get; }

    public Type Contract(string name) => _neurons.TryGetValue(name, out var contract)
        ? contract
        : throw new ArgumentException($"{name} is not a neuron contract of this brain.");

    // A neuron's Alias becomes the neuron type its contract resolves to, as instances record it; a contract several neurons
    // implement keeps its contract alias. A plain contract names its [Neuron] type; a connection contract (06-08) names none.
    // Any other contract is a still-grain's, whose type only the kernel composition (the grain factory's owner) resolves:
    // stillGrainType answers it, or null.
    public BrainType[] Addressed(Func<Type, string?> stillGrainType)
    {
        ArgumentNullException.ThrowIfNull(stillGrainType);
        return _addressed ??= [.. Entries.Select(entry =>
            entry.Kind == TypeKind.Neuron
            && !_screenTypes.Contains(entry.Name)
            && TypeOf(stillGrainType, _neurons[entry.Name]) is { } type
                ? entry with { Alias = type }
                : entry)];
    }

    private static string? TypeOf(Func<Type, string?> stillGrainType, Type contract)
        => DigitalBrain.Contracts.PlainNeuronContracts.Is(contract) ? DigitalBrain.Contracts.PlainNeuronContracts.TypeOf(contract) : stillGrainType(contract);

    // A legacy grain contract, or a migrated plain contract that names its [Neuron] type (06-03). IRetainsSignals is the legacy
    // retention side-interface every retaining grain adds, never a neuron of its own: once one grain is left implementing it
    // (06-08), Orleans resolves it to that grain's type and it would shadow the grain's own contract in listings.
    // A plain contract marked [Internal] as a whole (the connection index, 06-08) is module plumbing no edge caller reaches, so no
    // script compiles against it either.
    private static bool IsNeuron(Type type)
        => DigitalBrain.Contracts.PlainNeuronContracts.Is(type) && !type.IsDefined(typeof(DigitalBrain.Access.InternalAttribute), inherit: false);

    private static bool IsScreenNeuron(Type type, out string screenType)
    {
        if (type.IsClass && !type.IsAbstract
            && typeof(IScreen).IsAssignableFrom(type)
            && !type.IsDefined(typeof(DigitalBrain.Access.InternalAttribute), inherit: false)
            && type.GetCustomAttribute<NeuronAttribute>(inherit: false) is { Type.Length: > 0 } neuron)
        {
            screenType = neuron.Type;
            return true;
        }

        screenType = string.Empty;
        return false;
    }

    private static bool IsSignal(Type type)
        => !type.IsAbstract && typeof(DigitalBrain.Signal).IsAssignableFrom(type) && type != typeof(DigitalBrain.Signal)
            && type.IsDefined(typeof(DigitalBrain.SignalAttribute), inherit: false);

    private static string[] MethodsOf(Type contract)
        => [.. new[] { contract }.Concat(contract.GetInterfaces())
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => !method.IsSpecialName)
            .Select(method => method + (method.GetCustomAttribute<DescriptionAttribute>() is { } description ? " — " + description.Description : ""))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static BrainType Neuron(Type contract, string alias, string provider)
    {
        var name = contract.Name.StartsWith('I') ? contract.Name[1..] : contract.Name;
        return new(contract.FullName!, alias, TypeKind.Neuron, provider,
            contract.GetCustomAttribute<DescriptionAttribute>()?.Description ?? name, MethodsOf(contract),
            [.. contract.GetInterfaces().Where(IsNeuron).Select(type => type.FullName!).Order(StringComparer.Ordinal)]);
    }

    private static BrainType ScreenNeuron(Type screenClass, string alias, string provider)
    {
        var description = screenClass.GetCustomAttribute<DescriptionAttribute>()?.Description
            ?? ScreenTitleOf(screenClass)
            ?? screenClass.Name;
        return new(alias, alias, TypeKind.Neuron, provider, description, MethodsOf(typeof(IScreen)), [typeof(IScreen).FullName!]);
    }

    private static string? ScreenTitleOf(Type screenClass)
    {
        foreach (var attribute in screenClass.GetCustomAttributes(inherit: false))
        {
            var attributeType = attribute.GetType();
            if (!string.Equals(attributeType.Name, "ScreenAttribute", StringComparison.Ordinal)) { continue; }
            if (attributeType.GetProperty("Title", BindingFlags.Public | BindingFlags.Instance)?.GetValue(attribute) is string title
                && !string.IsNullOrWhiteSpace(title))
            {
                return title;
            }
        }
        return null;
    }

    private static BrainType Signal(Type signal, string provider)
        => new(signal.FullName!, NeuronAliases.Of(signal) ?? signal.GetCustomAttribute<DigitalBrain.SignalAttribute>(inherit: false)?.Alias ?? signal.FullName!, TypeKind.Signal, provider,
            signal.GetCustomAttribute<DescriptionAttribute>()?.Description ?? signal.Name,
            [.. signal.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.DeclaringType != typeof(DigitalBrain.Signal))
                .Select(property => TypeName(property.PropertyType) + " " + property.Name)], []);

    private static string TypeName(Type type) => type.IsGenericType
        ? type.Name.Split('`')[0] + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">"
        : type.Name;
}
