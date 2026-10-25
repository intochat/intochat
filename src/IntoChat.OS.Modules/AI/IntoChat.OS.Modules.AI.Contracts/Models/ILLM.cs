using IntoChat.OS.Modules.AI.Inference;

namespace IntoChat.OS.Modules.AI.Models;

[Alias("ai.llm"), Orleans.Metadata.DefaultGrainType("ai.llm")]
// One model serves every brain; each caller is metered and granted as its own brain.
[IntoChat.OS.Shared]
public interface ILLM : IAiMarker, IntoChat.OS.INeuron
{
    Task<ModelDescriptor> Describe(ModelSelection? selection = null);
    [ResponseTimeout("00:10:00")] Task<InferenceResult> Generate(InferenceRequest request, CancellationToken cancellationToken = default);
    [ResponseTimeout("00:10:00")] IAsyncEnumerable<InferenceUpdate> GenerateStreaming(InferenceRequest request, CancellationToken cancellationToken = default);
}
