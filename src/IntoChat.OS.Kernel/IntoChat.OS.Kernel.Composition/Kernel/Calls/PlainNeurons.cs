using System.Collections.Concurrent;
using System.Reflection;
using DigitalBrain.Invocation;
using IntoChat.OS.Runtime;

namespace IntoChat.OS.Kernel.Composition;

// Transitional (06-03, deleted in 06-08): the os surfaces reach a migrated plain [Neuron] by contract and os key through the
// runtime's host grains, over the same sink the cross-runtime calls use (LegacyGrainSink routes a plain contract to its host).
// The call carries no neuron caller: the surface's own caller context is the host's, not a neuron's.
internal sealed class PlainNeurons(IGrainFactory grains) : IPlainNeurons
{
    private static readonly MethodInfo CreateProxy = typeof(NeuronProxy).GetMethod(nameof(NeuronProxy.Create))!;
    private static readonly ConcurrentDictionary<Type, MethodInfo> Factories = new();

    public object Get(Type contract, string fullKey)
    {
        var id = ((IPlainNeurons)this).IdOf(contract, fullKey);
        LegacyGrainSink.Register(contract);
        return Factories.GetOrAdd(contract, static type => CreateProxy.MakeGenericMethod(type)).Invoke(null, [id, new LegacyGrainSink(grains, string.Empty)])!;
    }
}
