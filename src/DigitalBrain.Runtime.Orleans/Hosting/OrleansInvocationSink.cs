using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain.Invocation;
using Orleans;
using Orleans.Runtime;

namespace DigitalBrain.Runtime.Orleans.Hosting;

/// <summary>
/// The in-silo <see cref="IInvocationSink"/> behind <c>Get&lt;T&gt;(NeuronId)</c>: picks the host grain by the class's policy,
/// keys it <c>brain/type/key</c> and stamps the caller into RequestContext, where the host grain reads provenance.
/// </summary>
internal sealed class OrleansInvocationSink(IGrainFactory grains, HostedNeuronTypes types, NeuronId? caller) : IInvocationSink
{
    // Async methods: the RequestContext change stays inside the call and never leaks back into the caller's flow.
    public async Task<JsonElement?> Invoke(NeuronId target, RuntimeInvocation call, CancellationToken cancellationToken)
    {
        var host = Host(target, call);
        Stamp();
        return await host.Invoke(Stamped(call), cancellationToken).ConfigureAwait(false);
    }

    public async Task Post(NeuronId target, RuntimeInvocation call)
    {
        var host = Host(target, call);
        Stamp();
        await host.Post(Stamped(call)).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<JsonElement> Stream(NeuronId target, RuntimeInvocation call, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var host = Host(target, call);
        Stamp();
        await foreach (var item in host.Stream(Stamped(call), cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false)) { yield return item; }
    }

    /// <summary>The host grain for <paramref name="target"/>.</summary>
    /// <exception cref="BindingException">The type is not hosted, or the call names another neuron.</exception>
    internal INeuronHost Host(NeuronId target, RuntimeInvocation call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!types.TryGet(target.Type, out var neuronClass)) { throw new BindingException($"No neuron class is hosted for type '{target.Type}'."); }
        var key = target.ToString();
        if (!string.Equals(call.Key, key, StringComparison.Ordinal)) { throw new BindingException($"The call names {call.Key}, not {key}."); }
        var grainType = HostedNeuronTypes.GrainTypeOf(HostedNeuronTypes.PolicyOf(neuronClass));
        return grains.GetGrain<INeuronHost>(GrainId.Create(grainType, key));
    }

    private RuntimeInvocation Stamped(RuntimeInvocation call) => call with { Behavior = caller?.ToString() ?? string.Empty };

    private void Stamp()
    {
        if (caller is { } id) { RequestContext.Set(HostedNeuronContext.CallerKey, id.ToString()); }
        else { RequestContext.Remove(HostedNeuronContext.CallerKey); }
    }
}
