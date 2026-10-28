using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using Orleans.Concurrency;
using Orleans.Runtime;
using Microsoft.Extensions.DependencyInjection;
using DigitalBrain.Runtime.Orleans.Hosting;

namespace IntoChat.OS.Modules.Apps;

// The Creator services run outside any grain, so their model calls hop through this worker:
// grain-to-grain calls carry the kernel's trusted stamp, and no service ever mints one. The call
// lands on the model neuron of the brain the work is for.
[Alias("apps.creator-model"), Orleans.Metadata.DefaultGrainType("apps.creator-model")]
public interface ICreatorModel : IGrainWithIntegerKey
{
    [ResponseTimeout("00:10:00")] Task<string> Complete(string brainId, string model, string system, string prompt);
    [ResponseTimeout("00:10:00")] Task<InferenceResult> Generate(string brainId, string model, InferenceRequest request);
}

[GrainType("apps.creator-model"), StatelessWorker, Reentrant]
internal sealed class CreatorModelWorker : Grain, ICreatorModel
{
    // The model neurons are plain neurons (06-05); this worker stays a raw grain (allow-listed until Phase 12) and reaches them
    // through the kernel's plain-neuron seam, still from inside a grain call, so the trusted stamp flows on.
    private IServiceProvider Models => ServiceProvider;

    public Task<string> Complete(string brainId, string model, string system, string prompt)
        => CompleteModel(Models, brainId, model, system, prompt);

    public Task<InferenceResult> Generate(string brainId, string model, InferenceRequest request)
        => Resolve(Models, brainId, model).Generate(request);

    private static ILLM Resolve(IServiceProvider neurons, string brainId, string model)
    {
        ArgumentNullException.ThrowIfNull(neurons);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var marker = LLMModel.FindByMarkerName(model)?.Marker ?? throw new ArgumentException($"Unknown model '{model}'.", nameof(model));
        return (ILLM)SiloNeurons.Get(neurons, marker, ModelAddress.Key(brainId));
    }

    private static async Task<string> CompleteModel(IServiceProvider neurons, string brainId, string model, string system, string user, CancellationToken cancellationToken = default)
    {
        var result = await Resolve(neurons, brainId, model).Generate(new InferenceRequest(
        [
            new AiMessage("system", [new AiText(system)]),
            new AiMessage("user", [new AiText(user)]),
        ]), cancellationToken);
        return string.Concat(result.Messages.SelectMany(message => message.Content).OfType<AiText>().Select(content => content.Text)).Trim();
    }
}
