using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;

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

    public static IConnection GetConnection(this IServiceProvider plain, string key) => Get<IConnection>(plain, key);

    public static IConnectionInbound GetConnectionInbound(this IServiceProvider plain, string key) => Get<IConnectionInbound>(plain, key);

    // A connection contract (IConnection, IConnectionInbound or a connector's own) of the connection the key names.
    public static T Get<T>(this IServiceProvider plain, string key) where T : class
    {
        ArgumentNullException.ThrowIfNull(plain);
        return SiloNeurons.Get<T>(plain, IdFor(key));
    }

    // Addressing by a caller-supplied key: a canonical connection key resolves by its connector, but only when a composed module
    // declares that connector, so a crafted key can never activate an arbitrary neuron type. Any other key resolves by the
    // contract's own neuron type (a plain contract) and the neuron's own Key refuses it.
    /// <exception cref="ArgumentException">The key names a connector no composed module declares.</exception>
    public static T GetNeuron<T>(this IServiceProvider plain, IServiceProvider services, string key) where T : class
    {
        ArgumentNullException.ThrowIfNull(plain);
        ArgumentNullException.ThrowIfNull(services);
        if (!ConnectionKeys.Is(key)) { return SiloNeurons.Get<T>(plain, key); }
        string connector;
        try { connector = ConnectionKeys.Parse(key).ConnectorId; }
        catch (ArgumentException) { return SiloNeurons.Get<T>(plain, key); }
        if (!IsConnector(services, connector))
        { throw new ArgumentException($"No connection neuron serves connector '{connector}'.", nameof(key)); }
        return SiloNeurons.Get<T>(plain, IdFor(key));
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
