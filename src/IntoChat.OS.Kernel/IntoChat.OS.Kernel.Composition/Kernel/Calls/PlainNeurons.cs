using System.Collections.Concurrent;
using System.Reflection;
using DigitalBrain.Invocation;
using IntoChat.OS.Framework;
using IntoChat.OS.Runtime;

namespace IntoChat.OS.Kernel.Composition;

// Transitional (06-03, deleted in 06-08): the os surfaces reach a migrated plain [Neuron] by contract and os key through the
// runtime's host grains, over the same sink the cross-runtime calls use (LegacyGrainSink routes a plain contract to its host).
// The call carries no neuron caller: the surface's own caller context is the host's, not a neuron's.
internal sealed class PlainNeurons(IGrainFactory grains, ICallFilter? access = null, Microsoft.Extensions.Logging.ILogger<PlainNeuronCallFilter>? logger = null) : IPlainNeurons
{
    private static readonly MethodInfo CreateProxy = typeof(NeuronProxy).GetMethod(nameof(NeuronProxy.Create))!;
    private static readonly ConcurrentDictionary<Type, MethodInfo> Factories = new();

    // The policy PlainNeuronCallFilter applies to a call, applied before a stream starts; a call without a stamped caller passes.
    private Task StreamAccess(global::DigitalBrain.NeuronId target, RuntimeInvocation call)
        => access is not null && IntoChat.OS.Framework.CallerContextStamper.TryGet(out var caller)
            ? PlainNeuronCallFilter.Authorize(access, logger ?? (Microsoft.Extensions.Logging.ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, caller, target, call)
            : Task.CompletedTask;

    public object Get(Type contract, string fullKey)
    {
        var id = ((IPlainNeurons)this).IdOf(contract, fullKey);
        LegacyGrainSink.Register(contract);
        return Factories.GetOrAdd(contract, static type => CreateProxy.MakeGenericMethod(type)).Invoke(null, [id, new LegacyGrainSink(grains, string.Empty, StreamAccess)])!;
    }
}
