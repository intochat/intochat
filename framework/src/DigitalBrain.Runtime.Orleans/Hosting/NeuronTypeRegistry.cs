using DigitalBrain.Limits;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Scheduling;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using DigitalBrain.Kernel;
using DigitalBrain.Units;

namespace DigitalBrain.Runtime.Orleans.Hosting;

/// <summary>Which host grain runs a neuron class.</summary>
internal enum HostPolicy
{
    /// <summary><c>neuron.host</c>: one turn at a time, brain-affine, neutral attributes decide interleaving.</summary>
    Affine,

    /// <summary><c>neuron.host.reentrant</c>: a <see cref="ReentrantAttribute"/> class; every call interleaves.</summary>
    Reentrant,

    /// <summary><c>neuron.host.stateless</c>: a <see cref="StatelessNeuronAttribute"/> class; many activations.</summary>
    Stateless,
}

/// <summary>One hosted plain neuron class as the registry sees it.</summary>
/// <param name="Type">The frozen <see cref="NeuronAttribute"/> type name.</param>
/// <param name="Class">The class.</param>
/// <param name="StateName">The persisted state name: <see cref="NeuronAttribute.State"/> or <c>state</c>. Storage keys never move.</param>
/// <param name="Policy">The host grain policy.</param>
/// <param name="Contracts">The contracts the class implements, framework interfaces excluded.</param>
internal sealed record NeuronRegistration(string Type, Type Class, string StateName, HostPolicy Policy, IReadOnlyList<Type> Contracts);

/// <summary>
/// The module-wide registry of hosted plain neuron classes (06 D-01, D-13): frozen <see cref="NeuronAttribute"/> type name to
/// class, state name, policy and contracts, built from the composed module assemblies. Duplicate type names and
/// <see cref="App"/> classes fail startup. The maps are thread-safe: <see cref="RegisterState"/> and <see cref="UnregisterState"/>
/// change them while the silo runs, and every lookup reads the live map.
/// </summary>
internal sealed class NeuronTypeRegistry : DigitalBrain.Components.IHostedNeuronTypes
{
    public bool IsHosted(string neuronType) => !string.IsNullOrWhiteSpace(neuronType) && _types.ContainsKey(neuronType);

    public bool IsContractHosted(Type contract)
        => contract is not null && _types.Values.Any(r => r.Contracts.Contains(contract) || contract.IsAssignableFrom(r.Class));

    public bool TryGetNeuronClass(string neuronType, out Type? neuronClass) => TryGet(neuronType, out neuronClass);
    internal const string AffineGrainType = "neuron.host";
    internal const string ReentrantGrainType = "neuron.host.reentrant";
    internal const string StatelessGrainType = "neuron.host.stateless";

    /// <summary>The state name when the class sets none.</summary>
    internal const string DefaultStateName = "state";

    private readonly ConcurrentDictionary<string, NeuronRegistration> _types = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers the <c>[Neuron]</c> classes of <paramref name="assemblies"/>, the installs neuron (<c>synapse.installs</c>) and the time
    /// neuron (<c>time</c>) of every brain, and, under each <c>[State]</c> alias of their <see cref="SynapseCatalog"/>, the closed
    /// <see cref="UnitNeuron{TState}"/> with <see cref="HostPolicy.Affine"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Two classes claim one type name, or a <c>[Neuron]</c> class is an app.</exception>
    /// <exception cref="SynapseShapeException">A state alias is declared twice, or a synapse function has an unsupported shape.</exception>
    public NeuronTypeRegistry(IEnumerable<Assembly> assemblies)
        : this(assemblies, [])
    {
    }

    internal NeuronTypeRegistry(IEnumerable<Assembly> assemblies, IEnumerable<Type> selectedModules)
        : this(SelectedLoadable(Listed(assemblies), selectedModules).Concat([typeof(InstallsNeuron), typeof(OffersNeuron), typeof(SourcesNeuron), typeof(PoliciesNeuron), typeof(DigitalBrain.Scheduling.TimeNeuron), typeof(DigitalBrain.Operations.OperationNeuron), typeof(DigitalBrain.Confirmation.ConfirmationsNeuron), typeof(UnitDeployment), typeof(AsksNeuron), typeof(AskBatchNeuron)]))
    {
        var listed = Listed(assemblies);
        Catalog = SynapseCatalog.Scan(listed);
        Actions = ScanActions(listed, selectedModules);
        foreach (var (alias, record) in Catalog.States)
        {
            var neuronClass = typeof(UnitNeuron<>).MakeGenericType(record);
            if (_types.TryGetValue(alias, out var other))
            {
                throw new InvalidOperationException($"Neuron type '{alias}' is claimed by {other.Class.FullName} and the [State] record {record.FullName}.");
            }
            _types[alias] = new NeuronRegistration(alias, neuronClass, DefaultStateName, HostPolicy.Affine, []);
        }
    }

    /// <exception cref="InvalidOperationException">Two classes claim one type name.</exception>
    internal NeuronTypeRegistry(IEnumerable<Type> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        foreach (var type in candidates.Distinct())
        {
            if (!type.IsClass || type.GetCustomAttribute<NeuronAttribute>(inherit: false) is not { } neuron) { continue; }
            if (type.IsAbstract || type.IsGenericTypeDefinition || !typeof(Neuron).IsAssignableFrom(type)) { continue; }
            if (_types.TryGetValue(neuron.Type, out var other) && other.Class != type)
            {
                throw new InvalidOperationException($"Neuron type '{neuron.Type}' is claimed by {other.Class.FullName} and {type.FullName}.");
            }
            _ = CallPolicy.PredicateOf(type); // a misnamed predicate fails at registration, not on a call
            var registration = new NeuronRegistration(neuron.Type, type, neuron.State ?? DefaultStateName, PolicyOf(type), ContractsOf(type));
            _types[neuron.Type] = registration;
        }
    }

    /// <summary>
    /// The state records and synapse functions of the hosted assemblies at startup, empty for a registry built from types; the live
    /// catalog is the <see cref="ISynapseCatalogSource"/> seeded from it.
    /// </summary>
    public SynapseCatalog Catalog { get; } = SynapseCatalog.Scan();

    /// <summary>The promise-able actions declared by the <see cref="ModuleManifest"/> of every module in the hosted assemblies.</summary>
    public IReadOnlyList<ActionDescriptor> Actions { get; } = [];

    /// <summary>Creates a state neuron registration for the given alias and state record type.</summary>
    public static NeuronRegistration StateRegistration(string alias, Type stateRecord) =>
        new(alias, typeof(UnitNeuron<>).MakeGenericType(stateRecord), DefaultStateName, HostPolicy.Affine, []);

    /// <summary>The hosted type names.</summary>
    public IReadOnlyCollection<string> Names => [.. _types.Keys];

    /// <summary>The registrations.</summary>
    public IReadOnlyCollection<NeuronRegistration> Registrations => [.. _types.Values];

    /// <summary>The class registered for <paramref name="type"/>.</summary>
    public bool TryGet(string type, [NotNullWhen(true)] out Type? neuronClass)
    {
        neuronClass = _types.TryGetValue(type, out var registration) ? registration.Class : null;
        return neuronClass is not null;
    }

    /// <summary>The registration for <paramref name="type"/>.</summary>
    public bool TryGetRegistration(string type, [NotNullWhen(true)] out NeuronRegistration? registration) => _types.TryGetValue(type, out registration);

    /// <summary>The registration for <paramref name="type"/>; the error names the type.</summary>
    /// <exception cref="Invocation.BindingException">No class is hosted for the type.</exception>
    public NeuronRegistration Require(string type)
        => _types.TryGetValue(type, out var registration) ? registration
            : throw new Invocation.BindingException($"No neuron class is hosted for type '{type}'.");

    /// <summary>The host policy a class declares.</summary>
    public static HostPolicy PolicyOf(Type neuronClass)
    {
        ArgumentNullException.ThrowIfNull(neuronClass);
        if (neuronClass.IsDefined(typeof(StatelessNeuronAttribute), inherit: false)) { return HostPolicy.Stateless; }
        return neuronClass.IsDefined(typeof(ReentrantAttribute), inherit: true) ? HostPolicy.Reentrant : HostPolicy.Affine;
    }

    /// <summary>The host grain type for a policy.</summary>
    public static string GrainTypeOf(HostPolicy policy) => policy switch
    {
        HostPolicy.Reentrant => ReentrantGrainType,
        HostPolicy.Stateless => StatelessGrainType,
        _ => AffineGrainType,
    };

    private static bool IsState(NeuronRegistration registration)
        => registration.Class.IsGenericType && registration.Class.GetGenericTypeDefinition() == typeof(UnitNeuron<>);

    private static Type[] ContractsOf(Type neuronClass)
        => [.. neuronClass.GetInterfaces().Where(face => face.Assembly != typeof(Neuron).Assembly && face.Assembly != typeof(object).Assembly)];

    private static Assembly[] Listed(IEnumerable<Assembly> assemblies)
        => [.. (assemblies ?? throw new ArgumentNullException(nameof(assemblies))).Distinct()];

    private static IEnumerable<Type> Loadable(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error) { return error.Types.OfType<Type>(); }
    }

    internal static IEnumerable<Type> SelectedLoadable(IEnumerable<Assembly> assemblies, IEnumerable<Type> selectedModules)
    {
        var selectedByAssembly = selectedModules
            .Distinct()
            .GroupBy(static m => m.Assembly)
            .ToDictionary(static g => g.Key, static g => g.ToHashSet());

        foreach (var assembly in assemblies)
        {
            var types = Loadable(assembly).ToArray();
            if (!selectedByAssembly.TryGetValue(assembly, out var selectedInAssembly))
            {
                foreach (var t in types) { yield return t; }
                continue;
            }

            var declaredModules = types
                .Where(static t => t is { IsClass: true, IsAbstract: false }
                    && typeof(DigitalBrain.Modules.Module).IsAssignableFrom(t)
                    && t.IsDefined(typeof(ModuleAttribute), inherit: false))
                .ToArray();

            if (!HasDisjointNamespaces(declaredModules))
            {
                foreach (var t in types) { yield return t; }
                continue;
            }

            foreach (var t in types)
            {
                var owners = OwningModules(t, declaredModules);
                if (owners.Count > 0 && !owners.Any(selectedInAssembly.Contains)) { continue; }
                yield return t;
            }
        }
    }

    private static bool HasDisjointNamespaces(Type[] declaredModules)
    {
        var namespaces = declaredModules
            .Select(static m => m.Namespace)
            .Where(static ns => !string.IsNullOrEmpty(ns))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (namespaces.Length <= 1) { return false; }
        for (var i = 0; i < namespaces.Length; i++)
        {
            var a = namespaces[i]!;
            for (var j = 0; j < namespaces.Length; j++)
            {
                if (i == j) { continue; }
                var b = namespaces[j]!;
                if (b.Length > a.Length && b[a.Length] == '.' && b.StartsWith(a, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }
        return true;
    }

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

    private static IReadOnlyList<ActionDescriptor> ScanActions(IEnumerable<Assembly> assemblies, IEnumerable<Type> selectedModules)
    {
        var selectedByAssembly = selectedModules
            .Distinct()
            .GroupBy(static m => m.Assembly)
            .ToDictionary(static g => g.Key, static g => g.ToHashSet());
        var actions = new Dictionary<string, ActionDescriptor>(StringComparer.Ordinal);
        foreach (var assembly in assemblies)
        {
            var hasSelection = selectedByAssembly.TryGetValue(assembly, out var selectedInAssembly);
            foreach (var type in Loadable(assembly).Where(t => t is { IsAbstract: false, IsClass: true } && typeof(DigitalBrain.Modules.Module).IsAssignableFrom(t) && t.IsDefined(typeof(ModuleAttribute), false)))
            {
                if (hasSelection && !selectedInAssembly!.Contains(type)) { continue; }
                try
                {
                    foreach (var action in ModuleManifest.Of(type).Actions)
                    {
                        _ = actions.TryAdd(action.Name, action);
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { _ = error; }
            }
        }
        return [.. actions.Values];
    }
}
