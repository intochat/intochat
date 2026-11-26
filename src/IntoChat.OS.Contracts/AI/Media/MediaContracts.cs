using DigitalBrain;
using DigitalBrain.Contracts;
using IntoChat.OS;

namespace IntoChat.OS.Modules.AI.Media;

public enum MediaOperation { ImageGeneration, Embedding, SpeechRecognition, SpeechSynthesis }

public sealed record MediaCapabilities(
    MediaOperation Operation,
    bool Available,
    int MaxPayloadBytes,
    string? UnavailableReason = null,
    int MaxTextCharacters = 32_768,
    int MaxBatchItems = 1);

// Bounded inline media for stage one; no remote URL fetching or filesystem access.
public sealed record MediaPayload(byte[] Content, string MediaType);
public sealed record ImageGenerationRequest(string Prompt);
public sealed record ImageGenerationResult(Guid OperationId, MediaPayload Image, string Model);
public sealed record EmbeddingRequest(string[] Inputs);
public sealed record EmbeddingResult(Guid OperationId, float[][] Vectors);

[NeuronAlias("ai.media.IImageGenerator"), DefaultNeuronType("imagegeneratorneuron")]
public interface IImageGenerator
{
    [DigitalBrain.Contracts.ReadOnlyCall] Task<MediaCapabilities> Describe();
    [DigitalBrain.Contracts.CallTimeout(600000)] Task<ImageGenerationResult> Generate(ImageGenerationRequest request, CancellationToken cancellationToken = default);
}
[NeuronAlias("ai.media.IEmbeddingModel"), DefaultNeuronType("embeddingneuron")]
public interface IEmbeddingModel
{
    [DigitalBrain.Contracts.ReadOnlyCall] Task<MediaCapabilities> Describe();
    [DigitalBrain.Contracts.CallTimeout(600000)] Task<EmbeddingResult> Embed(EmbeddingRequest request, CancellationToken cancellationToken = default);
}

[Signal("ai.media.ImageGenerated")]
public sealed record ImageGenerated(Guid OperationId, string Model) : DigitalBrain.Signal;
[Signal("ai.media.EmbeddingsGenerated")]
public sealed record EmbeddingsGenerated(Guid OperationId, int Count) : DigitalBrain.Signal;
[Signal("ai.media.SpeechRecognized")]
public sealed record SpeechRecognized(Guid OperationId, string Model) : DigitalBrain.Signal;
[Signal("ai.media.SpeechSynthesized")]
public sealed record SpeechSynthesized(Guid OperationId, string Model) : DigitalBrain.Signal;
[Signal("ai.media.MediaOperationFailed")]
public sealed record MediaOperationFailed(Guid OperationId, MediaOperation Operation,
    string Code) : DigitalBrain.Signal;
