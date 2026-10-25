using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

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

/// <summary>
/// The minimal type map of the hosted plain neuron classes: frozen <see cref="NeuronAttribute"/> type name to class.
/// 04-11 replaces it with the full NeuronTypeRegistry.
/// </summary>
internal sealed class HostedNeuronTypes
{
    internal const string AffineGrainType = "neuron.host";
    internal const string ReentrantGrainType = "neuron.host.reentrant";
    internal const string StatelessGrainType = "neuron.host.stateless";

    // Process-wide view for the static [MayInterleave] predicate, which gets no services.
    private static readonly ConcurrentDictionary<string, Type> Process = new(StringComparer.Ordinal);

    private readonly Dictionary<string, Type> _types = new(StringComparer.Ordinal);

    /// <exception cref="InvalidOperationException">Two classes claim one type name.</exception>
    public HostedNeuronTypes(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        foreach (var type in assemblies.Distinct().SelectMany(Loadable))
        {
            if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition || !typeof(Neuron).IsAssignableFrom(type)) { continue; }
            if (type.GetCustomAttribute<NeuronAttribute>(inherit: false) is not { } neuron) { continue; }
            if (_types.TryGetValue(neuron.Type, out var other) && other != type)
            {
                throw new InvalidOperationException($"Neuron type '{neuron.Type}' is claimed by {other.FullName} and {type.FullName}.");
            }
            _types[neuron.Type] = type;
            Process[neuron.Type] = type;
        }
    }

    /// <summary>The hosted type names.</summary>
    public IReadOnlyCollection<string> Names => _types.Keys;

    /// <summary>The class registered for <paramref name="type"/>.</summary>
    public bool TryGet(string type, [NotNullWhen(true)] out Type? neuronClass) => _types.TryGetValue(type, out neuronClass);

    internal static bool TryGetAny(string type, [NotNullWhen(true)] out Type? neuronClass) => Process.TryGetValue(type, out neuronClass);

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

    private static IEnumerable<Type> Loadable(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error) { return error.Types.OfType<Type>(); }
    }
}
