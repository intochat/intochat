using DigitalBrain.AI;
using DigitalBrain.AI.Inference;
using DigitalBrain.AI.Models;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace DigitalBrain.Apps;

// The Creator services run outside any grain, so their model calls hop through this worker:
// grain-to-grain calls carry the kernel's trusted stamp, and no service ever mints one.
[Alias("apps.creator-model"), Orleans.Metadata.DefaultGrainType("apps.creator-model")]
public interface ICreatorModel : IGrainWithIntegerKey
{
    [ResponseTimeout("00:10:00")] Task<string> Complete(string model, string system, string prompt);
    [ResponseTimeout("00:10:00")] Task<InferenceResult> Generate(string model, InferenceRequest request);
}

[GrainType("apps.creator-model"), StatelessWorker, Reentrant]
internal sealed class CreatorModelWorker : Grain, ICreatorModel
{
    public Task<string> Complete(string model, string system, string prompt)
        => ModelAddress.Complete(GrainFactory, model, system, prompt);

    public Task<InferenceResult> Generate(string model, InferenceRequest request)
        => ModelAddress.Resolve(GrainFactory, model).Generate(request);
}
