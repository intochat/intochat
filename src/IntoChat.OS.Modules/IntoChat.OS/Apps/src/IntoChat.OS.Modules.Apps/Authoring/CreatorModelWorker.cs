using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.Apps;

internal static class CreatorModel
{
    public static async Task<string> Complete(IServiceProvider neurons, string brainId, string model, string system, string prompt, CancellationToken cancellationToken = default)
    {
        var result = await Resolve(neurons, brainId, model).Generate(new InferenceRequest(
        [
            new AiMessage("system", [new AiText(system)]),
            new AiMessage("user", [new AiText(prompt)]),
        ]), cancellationToken);
        return string.Concat(result.Messages.SelectMany(message => message.Content).OfType<AiText>().Select(content => content.Text)).Trim();
    }

    public static Task<InferenceResult> Generate(IServiceProvider neurons, string brainId, string model, InferenceRequest request, CancellationToken cancellationToken = default)
    {
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
