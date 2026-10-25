namespace IntoChat.OS.Runtime;

// Transitional (06-07b, R4-17; deleted with the Framework in 06-08): a plain [Neuron] in a module reaches a still-legacy grain
// neuron of its own brain with a typed proxy, without referencing the kernel composition, which implements it. The callee sees
// the calling neuron as its caller (LegacyNeuronCalls.CallerOf). Owners are a ratchet (LegacyNeuronsOwners.json) that 06-08
// drives to zero.
public interface ILegacyNeurons
{
    /// <exception cref="ArgumentException">id is in another brain than caller.</exception>
    T Get<T>(DigitalBrain.Spi.INeuronContext caller, DigitalBrain.NeuronId id) where T : class;
}

public static class LegacyNeuronAccess
{
    // The typed proxy of the legacy neuron id names, called as this neuron.
    /// <exception cref="ArgumentException">id is in another brain than this neuron.</exception>
    /// <exception cref="NotSupportedException">The host registers no ILegacyNeurons (only the kernel composition does).</exception>
    public static T Legacy<T>(this DigitalBrain.Spi.INeuronContext context, DigitalBrain.NeuronId id) where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        var legacy = context.Services.GetService(typeof(ILegacyNeurons)) as ILegacyNeurons
            ?? throw new NotSupportedException("This host reaches no legacy neurons: the kernel composition registers ILegacyNeurons.");
        return legacy.Get<T>(context, id);
    }
}
