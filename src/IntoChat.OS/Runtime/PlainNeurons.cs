using System.Reflection;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Runtime;

// Transitional (06-03, deleted with the Framework in 06-08): a migrated contract is a plain interface (D-11) that names the
// [Neuron] type it resolves to with [DefaultNeuronType]; it no longer derives INeuron, so the os surfaces that list and call
// contracts (the kernel type layer, the neuron surface, the contracts host) recognise it by that attribute.
public static class PlainNeuronContracts
{
    public static bool Is(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.IsInterface && !typeof(INeuron).IsAssignableFrom(type) && type.IsDefined(typeof(DefaultNeuronTypeAttribute), inherit: false);
    }

    // The neuron type the contract resolves to, or null when it is not a plain contract.
    public static string? TypeOf(Type contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return Is(contract) ? contract.GetCustomAttribute<DefaultNeuronTypeAttribute>(inherit: false)!.Type : null;
    }
}

// Transitional (06-03, deleted in 06-08): reaches a plain [Neuron] by its contract and the os full key "{brain}/{key}", through
// the runtime's host grains. The kernel composition implements it; the caller provenance is the host's (no neuron caller).
public interface IPlainNeurons
{
    // A typed proxy of contract (a plain contract) for the neuron "{brain}/{key}".
    /// <exception cref="ArgumentException">contract is not a plain contract, or fullKey names no brain.</exception>
    object Get(Type contract, string fullKey);

    // Get, typed (06-04: legacy grains and silo services reading a migrated control).
    /// <exception cref="ArgumentException">T is not a plain contract, or fullKey names no brain.</exception>
    T Get<T>(string fullKey) where T : class => (T)Get(typeof(T), fullKey);

    // The runtime neuron id ("brain/type/key") the os full key names for contract.
    /// <exception cref="ArgumentException">contract is not a plain contract, or fullKey names no brain.</exception>
    DigitalBrain.NeuronId IdOf(Type contract, string fullKey)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullKey);
        var type = PlainNeuronContracts.TypeOf(contract) ?? throw new ArgumentException($"{contract.Name} is not a plain neuron contract.", nameof(contract));
        var brain = NeuronAddress.ScopeOf(fullKey) ?? throw new ArgumentException($"'{fullKey}' names no brain: a plain neuron key is {{brain}}/{{key}}.", nameof(fullKey));
        return new DigitalBrain.NeuronId(new DigitalBrain.BrainId(brain), type, fullKey[(brain.Length + 1)..]);
    }
}
