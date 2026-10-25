using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.Spi;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

namespace IntoChat.OS.Kernel.Composition;


// Transitional (06-02, R4-12, deleted in 06-08): typed calls across the two runtimes during the family migration (call-site
// recipe: a caller of a migrated contract switches from GetGrain to Get<T>(global::DigitalBrain.NeuronId) in the same PR). A legacy Framework neuron
// reaches a plain neuron, or another legacy one, with this.Get<T>(id); a plain neuron reaches a legacy one with
// Context.LegacyGet<T>(id), because the runtime's own Context.Get<T> only knows hosted classes. Either way the callee sees the
// caller's neuron id: a plain callee as its runtime caller, a legacy callee through CallerOf().
public static class LegacyNeuronCalls
{
    public static T Get<T>(this IntoChat.OS.Framework.Neuron neuron, global::DigitalBrain.NeuronId id) where T : class
    {
        ArgumentNullException.ThrowIfNull(neuron);
        var grainId = neuron.GetGrainId();
        return Create<T>(neuron.GrainContext.ActivationServices.GetRequiredService<IGrainFactory>(), LegacyPublishers.Of(grainId), id);
    }

    public static T LegacyGet<T>(this INeuronContext context, global::DigitalBrain.NeuronId id) where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        if (id.Brain != context.Id.Brain) { throw new ArgumentException($"Neuron {id} belongs to another brain than {context.Id}.", nameof(id)); }
        return Create<T>(context.Services.GetRequiredService<IGrainFactory>(), context.Id.ToString(), id);
    }

    // The neuron id of the caller of the current call, as the runtime carries it (empty when the call came from no neuron).
    public static string CallerOf() => RequestContext.Get(LegacyGrainSink.CallerKey) as string ?? string.Empty;

    private static T Create<T>(IGrainFactory grains, string caller, global::DigitalBrain.NeuronId id) where T : class
    {
        LegacyGrainSink.Register(typeof(T));
        return NeuronProxy.Create<T>(id, new LegacyGrainSink(grains, caller));
    }
}
