using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.Apps;

internal static class CreatorModel
{
    public static async Task<string> Complete(IServiceProvider neurons, string brainId, string model, string system, string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(neurons);
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var result = await Resolve(neurons, brainId, model).Generate(new InferenceRequest(
        [
            new AiMessage("system", [new AiText(system)]),
            new AiMessage("user", [new AiText(prompt)]),
        ]), cancellationToken);
        var text = string.Concat(result.Messages.SelectMany(message => message.Content).OfType<AiText>().Select(content => content.Text)).Trim();
        return text;
    }

    public static Task<InferenceResult> Generate(IServiceProvider neurons, string brainId, string model, InferenceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(neurons);
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(request);
        return Resolve(neurons, brainId, model).Generate(request, cancellationToken);
    }

    private static ILLM Resolve(IServiceProvider neurons, string brainId, string model)
    {
        ArgumentNullException.ThrowIfNull(neurons);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var marker = LLMModel.Find(model)?.Marker ?? throw new ArgumentException($"Unknown model '{model}'.", nameof(model));
        return (ILLM)SiloNeurons.Get(neurons, marker, ModelAddress.Key(brainId));
    }
}

// The Creator's model calls, as a seam: a scripted model stands here in facts.
internal interface ICreatorModel
{
    Task<string> Complete(string brainId, string model, string system, string prompt, CancellationToken ct);

    Task<InferenceResult> Generate(string brainId, string model, InferenceRequest request, CancellationToken ct);
}

// The brain's own model neurons with deterministic offline synthesizer fallback when unconfigured or offline.
internal sealed class SiloCreatorModel(IServiceProvider neurons) : ICreatorModel
{
    public async Task<string> Complete(string brainId, string model, string system, string prompt, CancellationToken ct)
    {
        try
        {
            return await CreatorModel.Complete(neurons, brainId, model, system, prompt, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (OfflineUnitSynthesizer.ShouldFallback(error))
        {
            return await OfflineUnitSynthesizer.CompleteAsync(system, prompt, ct).ConfigureAwait(false);
        }
    }

    public async Task<InferenceResult> Generate(string brainId, string model, InferenceRequest request, CancellationToken ct)
    {
        try
        {
            return await CreatorModel.Generate(neurons, brainId, model, request, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (OfflineUnitSynthesizer.ShouldFallback(error))
        {
            return await OfflineUnitSynthesizer.GenerateAsync(model, request, ct).ConfigureAwait(false);
        }
    }
}
