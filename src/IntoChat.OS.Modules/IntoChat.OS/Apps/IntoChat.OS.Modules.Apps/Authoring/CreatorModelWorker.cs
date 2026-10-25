using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using Orleans.Concurrency;
using Orleans.Runtime;
using Microsoft.Extensions.DependencyInjection;

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
    private IntoChat.OS.Runtime.IPlainNeurons Models => ServiceProvider.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>();

    public Task<string> Complete(string brainId, string model, string system, string prompt)
        => ModelAddress.Complete(Models, brainId, model, system, prompt);

    public Task<InferenceResult> Generate(string brainId, string model, InferenceRequest request)
        => ModelAddress.Resolve(Models, brainId, model).Generate(request);
}
