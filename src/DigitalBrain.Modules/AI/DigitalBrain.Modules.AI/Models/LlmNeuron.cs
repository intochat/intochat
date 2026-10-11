using System.Runtime.CompilerServices;
using DigitalBrain.AI.Inference;
using DigitalBrain.AI.Models.Signals;
using DigitalBrain.Framework;
using Orleans.Concurrency;
using Orleans.Serialization.Invocation;

namespace DigitalBrain.AI.Models;

// A brain's model calls run concurrently: only Describe and Generate interleave, with each other.
// Watch/Unwatch stay exclusive so the observer set never changes under a running publish, and
// publishes are serialized because ObserverManager.Notify enumerates (and prunes) across awaits.
[MayInterleave(nameof(Interleaves))]
public abstract class LlmNeuronBase(InferenceService inference, Type? marker = null) : Neuron, ILLM
{
    private readonly SemaphoreSlim _publishing = new(1, 1);

    public static bool Interleaves(IInvokable request)
        => request.GetInterfaceType() is { } contract && typeof(ILLM).IsAssignableFrom(contract)
            && request.GetMethodName() is nameof(ILLM.Generate) or nameof(ILLM.Describe);

    public Task<ModelDescriptor> Describe(ModelSelection? selection = null)
        => inference.Describe(Selection(selection), marker);
    public async Task<InferenceResult> Generate(InferenceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = Guid.NewGuid().ToString("N");
        var key = this.GetPrimaryKeyString();
        await Publish(new InferenceStarted(key, id));
        try
        {
            var result = await inference.Generate(request with { Model = Selection(request.Model) }, marker, cancellationToken);
            await Publish(new InferenceCompleted(key, id));
            return result;
        }
        catch (Exception error)
        {
            await Publish(new InferenceFailed(key, id, error is OperationCanceledException ? "Cancelled" : error.GetType().Name));
            throw;
        }
    }
    public async IAsyncEnumerable<InferenceUpdate> GenerateStreaming(InferenceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = Guid.NewGuid().ToString("N");
        var key = this.GetPrimaryKeyString();
        var completed = false;
        await Publish(new InferenceStarted(key, id));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await foreach (var update in inference.GenerateStreaming(request with { Model = Selection(request.Model) }, marker, lifetime.Token))
            { yield return update; }
            completed = true;
        }
        finally
        {
            await lifetime.CancelAsync();
            if (completed) { await Publish(new InferenceCompleted(key, id)); }
            else { await Publish(new InferenceFailed(key, id, "Stream cancelled, failed or abandoned")); }
        }
    }

    private async Task Publish(Signal signal)
    {
        await _publishing.WaitAsync();
        try { await PublishAsync(signal); }
        finally { _publishing.Release(); }
    }

    private ModelSelection? Selection(ModelSelection? selection)
    {
        if (selection is not null) { return selection; }
        var key = this.GetPrimaryKeyString();
        // A brain's model key, like "default", names no profile: the deployment default (or the marker's model).
        var unnamed = key == "default" || BrainKey.ScopeOf(key) is { } brain && key == ModelAddress.Key(brain);
        return unnamed || (marker is not null && !inference.HasProfile(key)) ? null : new(key);
    }
}

[GrainType("ai.llm")]
internal sealed class LlmNeuron(InferenceService inference) : LlmNeuronBase(inference);
