using IntoChat.OS;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.AI.Media;

internal abstract class MediaNeuron : DigitalBrain.Neurons.Neuron
{
    public const int MaxPayloadBytes = 8 * 1024 * 1024;
    protected static MediaCapabilities Capabilities(MediaOperation operation, Func<bool> available)
    {
        try { return new(operation, available(), MaxPayloadBytes); }
        catch (InvalidOperationException) { return new(operation, false, MaxPayloadBytes, "Provider configuration is unavailable."); }
    }
    internal static void ValidateText(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > 32_768) { throw new ArgumentException("Text must not exceed 32768 characters."); }
    }
    internal static void ValidatePayload(MediaPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(payload.Content);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload.MediaType);
        if (payload.Content.Length is 0 or > MaxPayloadBytes) { throw new ArgumentException("Media must contain between 1 byte and 8 MiB."); }
    }
    protected async Task<T> Execute<T>(MediaOperation operation, CancellationToken ct,
        Func<Guid, Task<T>> action, Func<T, DigitalBrain.Signal> completed)
    {
        var id = Guid.NewGuid();
        T result;
        try { ct.ThrowIfCancellationRequested(); result = await action(id); ct.ThrowIfCancellationRequested(); }
        catch (Exception error)
        {
            await Publish(new MediaOperationFailed(id, operation,
                error is OperationCanceledException ? "cancelled" : error is NotSupportedException ? "unavailable" : "failed"));
            throw;
        }
        await Publish(completed(result));
        return result;
    }
}

[DigitalBrain.Neurons.Neuron("imagegeneratorneuron")]
internal sealed class ImageGeneratorNeuron(IServiceProvider services) : MediaNeuron, IImageGenerator
{
    public async Task<MediaCapabilities> Describe()
    {
        var images = services.GetService<IImageGeneration>();
        return new(MediaOperation.ImageGeneration, images is not null && (images is not IConditionallyAvailable conditional || await conditional.IsAvailableAsync()), MaxPayloadBytes);
    }
    public Task<ImageGenerationResult> Generate(ImageGenerationRequest request, CancellationToken cancellationToken = default)
        => Execute(MediaOperation.ImageGeneration, cancellationToken, async id =>
        {
            ArgumentNullException.ThrowIfNull(request); ValidateText(request.Prompt);
            var service = services.GetService<IImageGeneration>() ?? throw new NotSupportedException("Configure an image generation provider.");
            var result = await service.GenerateAsync(request.Prompt, cancellationToken);
            var payload = new MediaPayload(result.Content, result.MediaType); ValidatePayload(payload);
            return new ImageGenerationResult(id, payload, result.Model);
        }, result => new ImageGenerated(result.OperationId, result.Model));
}

[DigitalBrain.Neurons.Neuron("embeddingneuron")]
internal sealed class EmbeddingNeuron(IServiceProvider services) : MediaNeuron, IEmbeddingModel
{
    public Task<MediaCapabilities> Describe() => Task.FromResult(Capabilities(MediaOperation.Embedding,
        () => services.GetService<IEmbeddingGenerator<string, Embedding<float>>>() is not null) with
    { MaxBatchItems = 128 });
    public Task<EmbeddingResult> Embed(EmbeddingRequest request, CancellationToken cancellationToken = default)
        => Execute(MediaOperation.Embedding, cancellationToken, async id =>
        {
            ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Inputs);
            if (request.Inputs.Length is 0 or > 128) { throw new ArgumentException("Supply between 1 and 128 inputs."); }
            foreach (var text in request.Inputs) { ValidateText(text); }
            var service = services.GetService<IEmbeddingGenerator<string, Embedding<float>>>()
                ?? throw new NotSupportedException("Configure an embedding provider.");
            var generated = await service.GenerateAsync(request.Inputs, cancellationToken: cancellationToken);
            if (generated.Count != request.Inputs.Length) { throw new InvalidOperationException("Provider returned the wrong number of embeddings."); }
            if (generated.Sum(x => (long)x.Vector.Length * sizeof(float)) > MaxPayloadBytes) { throw new InvalidOperationException("Embedding response exceeds 8 MiB."); }
            return new EmbeddingResult(id, generated.Select(x => x.Vector.ToArray()).ToArray());
        }, result => new EmbeddingsGenerated(result.OperationId, result.Vectors.Length));
}
