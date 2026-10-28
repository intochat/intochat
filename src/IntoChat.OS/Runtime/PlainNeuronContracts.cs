using System.Reflection;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Runtime;

// A migrated contract is a plain interface that names the [Neuron] type it resolves to with [DefaultNeuronType]. It does not
// derive INeuron, so the surfaces that list and call contracts recognise it by that attribute.
public static class PlainNeuronContracts
{
    public static bool Is(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.IsInterface && !typeof(INeuron).IsAssignableFrom(type)
            && (type.IsDefined(typeof(DefaultNeuronTypeAttribute), inherit: false) || IsConnection(type));
    }

    // A contract every connection neuron may implement (IConnection, ILegacyStorage): it names no neuron type, the
    // connection key "{brain}/connection/{connector}/{id}" does.
    public static bool IsConnection(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.IsInterface && type.IsDefined(typeof(ConnectionContractAttribute), inherit: false);
    }

    // The connector a canonical connection key names, or null.
    public static string? ConnectorOf(string fullKey)
        => fullKey.Split('/') is [var brain, "connection", var connector, var id] && NeuronAddress.IsBrainId(brain) && connector.Length > 0 && id.Length > 0
            ? Uri.UnescapeDataString(connector) : null;

    // The neuron type the contract resolves to, or null when it is not a plain contract.
    public static string? TypeOf(Type contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return Is(contract) ? contract.GetCustomAttribute<DefaultNeuronTypeAttribute>(inherit: false)?.Type : null;
    }

    // The runtime neuron id the os full key names for contract.
    public static DigitalBrain.NeuronId IdOf(Type contract, string fullKey)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullKey);
        var type = TypeOf(contract)
            ?? (IsConnection(contract)
                ? ConnectorOf(fullKey) ?? throw new ArgumentException($"{contract.Name} addresses a connection: '{fullKey}' is not a connection key.", nameof(fullKey))
                : throw new ArgumentException($"{contract.Name} is not a plain neuron contract.", nameof(contract)));
        var brain = NeuronAddress.ScopeOf(fullKey) ?? throw new ArgumentException($"'{fullKey}' names no brain: a plain neuron key is {{brain}}/{{key}}.", nameof(fullKey));
        return new DigitalBrain.NeuronId(new DigitalBrain.BrainId(brain), type, fullKey[(brain.Length + 1)..]);
    }
}

// Marks a plain contract every connection neuron may implement: the connection key names its neuron type.
[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class ConnectionContractAttribute : Attribute;
