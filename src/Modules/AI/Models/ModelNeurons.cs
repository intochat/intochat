using DigitalBrain;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.AI.Inference;

namespace IntoChat.OS.Modules.AI.Models;

// Model neurons are reached through the single ILLM contract keyed by {brain}/llm/{modelId}.
// The contracts assembly cannot name the silo host, so callers outside that assembly use this.
internal static class ModelNeurons
{
    public static ILLM Resolve(IServiceProvider neurons, BrainId brain, ModelId model)
    {
        ArgumentNullException.ThrowIfNull(neurons);
        return SiloNeurons.Get<ILLM>(neurons, ILLM.Key(brain.Value, model));
    }

    public static ILLM Resolve(IServiceProvider neurons, BrainId brain, string model)
    {
        ArgumentNullException.ThrowIfNull(neurons);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var card = KnownModels.Find(model) ?? throw new ArgumentException($"Unknown model '{model}'.", nameof(model));
        return Resolve(neurons, brain, card.Id);
    }

    public static async Task<string> Complete(IServiceProvider neurons, BrainId brain, string model, string system, string user, CancellationToken cancellationToken = default)
    {
        var result = await Resolve(neurons, brain, model).Generate(new InferenceRequest(
        [
            new AiMessage("system", [new AiText(system)]),
            new AiMessage("user", [new AiText(user)]),
        ]), cancellationToken);
        return string.Concat(result.Messages.SelectMany(message => message.Content).OfType<AiText>().Select(content => content.Text)).Trim();
    }
}
