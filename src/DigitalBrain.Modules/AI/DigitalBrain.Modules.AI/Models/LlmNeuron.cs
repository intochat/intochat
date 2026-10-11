using System.Runtime.CompilerServices;
using DigitalBrain.AI.Inference;
using DigitalBrain.AI.Models.Signals;
using DigitalBrain.Framework;

namespace DigitalBrain.AI.Models;

public abstract class LlmNeuronBase(InferenceService inference, Type? marker = null) : Neuron, ILLM
{
    public Task<ModelDescriptor> Describe(ModelSelection? selection = null)
        => inference.Describe(Selection(selection), marker);
    public async Task<InferenceResult> Generate(InferenceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = Guid.NewGuid().ToString("N");
        var key = this.GetPrimaryKeyString();
        await PublishAsync(new InferenceStarted(key, id));
        try
        {
            var result = await inference.Generate(request with { Model = Selection(request.Model) }, marker, cancellationToken);
            await PublishAsync(new InferenceCompleted(key, id));
            return result;
        }
        catch (Exception error)
        {
            await PublishAsync(new InferenceFailed(key, id, error is OperationCanceledException ? "Cancelled" : error.GetType().Name));
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
        await PublishAsync(new InferenceStarted(key, id));
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
            if (completed) { await PublishAsync(new InferenceCompleted(key, id)); }
            else { await PublishAsync(new InferenceFailed(key, id, "Stream cancelled, failed or abandoned")); }
        }
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
