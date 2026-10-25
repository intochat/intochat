using IntoChat.OS.Runtime;

namespace IntoChat.OS.Kernel.Composition;

// Transitional (06-07b, R4-17; deleted in 06-08): the os-level face of LegacyNeuronCalls.LegacyGet for module neurons.
internal sealed class LegacyNeurons : ILegacyNeurons
{
    public T Get<T>(global::DigitalBrain.Spi.INeuronContext caller, global::DigitalBrain.NeuronId id) where T : class => caller.LegacyGet<T>(id);
}
