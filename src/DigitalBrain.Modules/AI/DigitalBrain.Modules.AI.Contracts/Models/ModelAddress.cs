using DigitalBrain.AI.Inference;

namespace DigitalBrain.AI.Models;

// Turns an LLM marker name used in configuration, such as "IGpt56Luna", into its neuron inside a brain.
// ILLM serves every caller as its own brain, but its key still belongs to one brain like every neuron key:
// the inference signals it publishes journal into that brain, and a machine refuses a key outside every brain.
public static class ModelAddress
{
    // A brain's model neuron: the deployment's default model for the marker it is resolved as.
    public static string Key(string brainId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        if (brainId.Contains('/')) { throw new ArgumentException($"'{brainId}' is a neuron key, not a brain id.", nameof(brainId)); }
        return $"{brainId}/models";
    }

    public static bool IsKnown(string model) => LLMModel.FindByMarkerName(model) is not null;

    public static ILLM Resolve(IGrainFactory grains, string brainId, string model)
    {
        ArgumentNullException.ThrowIfNull(grains);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var marker = LLMModel.FindByMarkerName(model)?.Marker ?? throw new ArgumentException($"Unknown model '{model}'.", nameof(model));
        return (ILLM)grains.GetGrain(marker, Key(brainId));
    }

    public static async Task<string> Complete(IGrainFactory grains, string brainId, string model, string system, string user, CancellationToken cancellationToken = default)
    {
        var result = await Resolve(grains, brainId, model).Generate(new InferenceRequest(
        [
            new AiMessage("system", [new AiText(system)]),
            new AiMessage("user", [new AiText(user)]),
        ]), cancellationToken);
        return string.Concat(result.Messages.SelectMany(message => message.Content).OfType<AiText>().Select(content => content.Text)).Trim();
    }
}
