using DigitalBrain.Access;
using DigitalBrain.Contracts;
using IntoChat.OS.Modules.AI.Inference;

namespace IntoChat.OS.Modules.AI.Models;

[NeuronAlias("ai.llm"), DefaultNeuronType("ai.llm")]
// One model serves every brain; each caller is metered and granted as its own brain.
[DigitalBrain.Access.Shared]
public interface ILLM
{
    static string Key(string brainId, ModelId model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        if (brainId.Contains('/')) { throw new ArgumentException($"'{brainId}' is a neuron key, not a brain id.", nameof(brainId)); }
        ArgumentException.ThrowIfNullOrWhiteSpace(model.Value);
        return $"{brainId}/llm/{model.Value}";
    }

    Task<ModelDescriptor> Describe(ModelSelection? selection = null);
    [DigitalBrain.Contracts.CallTimeout(600000)] Task<InferenceResult> Generate(InferenceRequest request, CancellationToken cancellationToken = default);
    [DigitalBrain.Contracts.CallTimeout(600000)] IAsyncEnumerable<InferenceUpdate> GenerateStreaming(InferenceRequest request, CancellationToken cancellationToken = default);
}
