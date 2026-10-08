using DigitalBrain.AI.Inference;

namespace DigitalBrain.AI.Models;

// Turns an LLM marker name used in configuration, such as "IGpt56Luna", into its neuron.
public static class ModelAddress
{
    public static bool IsKnown(string model) => LLMModel.FindByMarkerName(model) is not null;

    public static ILLM Resolve(IGrainFactory grains, string model)
    {
        ArgumentNullException.ThrowIfNull(grains);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var marker = LLMModel.FindByMarkerName(model)?.Marker ?? throw new ArgumentException($"Unknown model '{model}'.", nameof(model));
        return (ILLM)grains.GetGrain(marker, "default");
    }

    public static async Task<string> Complete(IGrainFactory grains, string model, string system, string user, CancellationToken cancellationToken = default)
    {
        var result = await Resolve(grains, model).Generate(new InferenceRequest(
        [
            new AiMessage("system", [new AiText(system)]),
            new AiMessage("user", [new AiText(user)]),
        ]), cancellationToken);
        return string.Concat(result.Messages.SelectMany(message => message.Content).OfType<AiText>().Select(content => content.Text)).Trim();
    }
}
