using DigitalBrain;
using Orleans.Concurrency;

namespace DigitalBrain.Authority.Workshop;

// The Workshop edits the OS's own codebase: it writes files inside the configured root and builds
// a project there. Not a neuron, so no script, app or router can reach it -- the host gates who
// may. The caller sequences Write -> Build -> IDeployment.Restart.
[Alias("platform.workshop"), Orleans.Metadata.DefaultGrainType("platform.workshop")]
public interface IWorkshop : IGrainWithStringKey
{
    [AlwaysInterleave] Task<WorkshopResult> Write(string path, string content, CancellationToken cancellationToken = default);

    [AlwaysInterleave, ResponseTimeout("00:10:00")] Task<WorkshopResult> Build(string project, CancellationToken cancellationToken = default);
}

[GenerateSerializer, Alias("platform.workshop-result")]
public sealed record WorkshopResult([property: Id(0)] bool Success, [property: Id(1)] string Output);
