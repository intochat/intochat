using IntoChat.OS.Connections.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Runtime;

namespace IntoChat.OS.Connections.Platform.Connections;

// Every connector's neuron implements IConnection, so a contract alone names no neuron type once two connectors are composed.
// The key does: each connection neuron is [Neuron(connectorId)] (06-08: plain neurons, R4-21(2) — the id's type picks the class).
public static class ConnectionGrains
{
    // The runtime neuron id a canonical connection key names: the connector is its type.
    /// <exception cref="ArgumentException">The key is not a canonical connection key.</exception>
    public static DigitalBrain.NeuronId IdFor(string key)
    {
        var (brain, connector, _) = ConnectionKeys.Parse(key);
        return new DigitalBrain.NeuronId(new DigitalBrain.BrainId(brain), connector, key[(brain.Length + 1)..]);
    }

    public static IConnection GetConnection(this IPlainNeurons plain, string key) => Get<IConnection>(plain, key);

    public static IConnectionInbound GetConnectionInbound(this IPlainNeurons plain, string key) => Get<IConnectionInbound>(plain, key);

    // A connection contract (IConnection, IConnectionInbound or a connector's own) of the connection the key names.
    public static T Get<T>(this IPlainNeurons plain, string key) where T : class
    {
        ArgumentNullException.ThrowIfNull(plain);
        return plain.Get<T>(IdFor(key));
    }

    // Addressing by a caller-supplied key: a canonical connection key resolves by its connector, but only when a composed module
    // declares that connector, so a crafted key can never activate an arbitrary neuron type. Any other key resolves by the
    // contract's own neuron type (a plain contract) and the neuron's own Key refuses it.
    /// <exception cref="ArgumentException">The key names a connector no composed module declares.</exception>
    public static T GetNeuron<T>(this IPlainNeurons plain, IServiceProvider services, string key) where T : class
    {
        ArgumentNullException.ThrowIfNull(plain);
        ArgumentNullException.ThrowIfNull(services);
        if (!ConnectionKeys.Is(key)) { return plain.Get<T>(key); }
        string connector;
        try { connector = ConnectionKeys.Parse(key).ConnectorId; }
        catch (ArgumentException) { return plain.Get<T>(key); }
        if (!IsConnector(services, connector))
        { throw new ArgumentException($"No connection neuron serves connector '{connector}'.", nameof(key)); }
        return plain.Get<T>(IdFor(key));
    }

    // Whether a composed module declares the connector and a hosted [Neuron] class serves it: a connector can be declared and
    // offered with no connection neuron (an api-key connector a provider reads directly), and its key addresses nothing.
    public static bool IsConnector(IServiceProvider services, string connector)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService<IReadOnlyList<ConnectorDefinition>>()?.Any(definition => definition.Id == connector) != true) { return false; }
        return services.GetService<ConnectionNeuronTypes>() is not { } hosted || hosted.Types.Contains(connector);
    }
}

// The neuron types of the hosted connection neurons (06-08): every [Neuron] class of the composed DigitalBrain modules that is a
// connection. Its type is the connector id its keys name. Each carries the capabilities its module's ModuleManifest provides (05-07).
public sealed class ConnectionNeuronTypes
{
    private readonly Dictionary<string, (Type Neuron, IReadOnlyList<DigitalBrain.Capabilities.CapabilityId> Provides)> _connectors = new(StringComparer.Ordinal);

    public ConnectionNeuronTypes(IEnumerable<Type> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        foreach (var module in modules.Distinct())
        {
            var provides = DigitalBrain.ModuleManifest.Of(module).Provides;
            foreach (var type in module.Assembly.GetTypes())
            {
                if (type is not { IsClass: true, IsAbstract: false } || !typeof(IConnection).IsAssignableFrom(type)) { continue; }
                if (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<DigitalBrain.NeuronAttribute>(type)?.Type is not { } connector) { continue; }
                // Two modules in one assembly see the same neuron classes: the first module that provides something wins.
                if (!_connectors.TryGetValue(connector, out var known) || (known.Provides.Count == 0 && provides.Count > 0))
                { _connectors[connector] = (type, provides); }
            }
        }
        Types = _connectors.Keys.ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlySet<string> Types { get; }

    // The [Neuron] class serving connector, or null when none is hosted.
    public Type? NeuronOf(string connector) => _connectors.TryGetValue(connector, out var entry) ? entry.Neuron : null;

    // The capabilities the module of connector's neuron provides (its ModuleManifest.Provides); empty when none is hosted.
    public IReadOnlyList<DigitalBrain.Capabilities.CapabilityId> ProvidesOf(string connector)
        => _connectors.TryGetValue(connector, out var entry) ? entry.Provides : [];
}
