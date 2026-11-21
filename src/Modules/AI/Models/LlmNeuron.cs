using System.Runtime.CompilerServices;
using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models.Signals;

namespace IntoChat.OS.Modules.AI.Models;

// One LLM neuron for every model, keyed either {brain}/models (the brain's Chat role) or
// {brain}/llm/{modelId} (pinned to that card). Calls interleave concurrently; signal publishing
// takes one gate so journaled inference signals stay ordered.
[Neuron("ai.llm")]
[DigitalBrain.Neurons.InterleavesWhen(nameof(Interleaves))]
internal sealed class LlmNeuron(IModelInference inference) : StatelessAiNeuron, ILLM
{
    private const string ModelKeyPrefix = "llm/";
    private readonly SemaphoreSlim _publishing = new(1, 1);

    public static bool Interleaves(RuntimeInvocation request)
        => request.Method is nameof(ILLM.Generate) or nameof(ILLM.Describe);

    public Task<ModelDescriptor> Describe(ModelSelection? selection = null)
        => inference.Describe(Selection(selection), PinnedModel, CancellationToken.None, Brain);

    public async Task<InferenceResult> Generate(InferenceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = Guid.NewGuid().ToString("N");
        var key = FullKey;
        await Signal(new InferenceStarted(key, id));
        try
        {
            var result = await inference.Generate(request with { Model = Selection(request.Model) }, PinnedModel, cancellationToken, Brain);
            await Signal(new InferenceCompleted(key, id));
            return result;
        }
        catch (Exception error)
        {
            await Signal(new InferenceFailed(key, id, error is OperationCanceledException ? "Cancelled" : error.GetType().Name));
            throw;
        }
    }

    public async IAsyncEnumerable<InferenceUpdate> GenerateStreaming(
        InferenceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = Guid.NewGuid().ToString("N");
        var key = FullKey;
        var completed = false;
        await Signal(new InferenceStarted(key, id));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await foreach (var update in inference.GenerateStreaming(request with { Model = Selection(request.Model) }, PinnedModel, lifetime.Token, Brain))
            {
                yield return update;
            }
            completed = true;
        }
        finally
        {
            await lifetime.CancelAsync();
            if (completed) { await Signal(new InferenceCompleted(key, id)); }
            else { await Signal(new InferenceFailed(key, id, "Stream cancelled, failed or abandoned")); }
        }
    }

    protected override Task AdmitSynapse(DigitalBrain.NeuronId subscriber, IReadOnlyList<Type> signals)
    {
        if (subscriber.Brain != Id.Brain)
        { throw new UnauthorizedAccessException($"Neuron {subscriber} of another brain may not subscribe to the model {Id}."); }
        return Task.CompletedTask;
    }

    private async Task Signal(DigitalBrain.Signal signal)
    {
        await _publishing.WaitAsync();
        try { await Publish(signal); }
        finally { _publishing.Release(); }
    }

    private string Brain => Id.Brain.Value;

    private ModelId? PinnedModel =>
        Id.Key.StartsWith(ModelKeyPrefix, StringComparison.Ordinal) && Id.Key.Length > ModelKeyPrefix.Length
            ? new ModelId(Id.Key[ModelKeyPrefix.Length..])
            : null;

    private ModelSelection? Selection(ModelSelection? selection)
    {
        if (selection is not null) { return selection; }
        var unnamed = FullKey == ModelAddress.Key(Brain);
        return unnamed || (PinnedModel is not null && !inference.HasProfile(Id.Key)) ? null : new(Id.Key);
    }
}
