using DigitalBrain;
using Orleans.Concurrency;

namespace DigitalBrain.Flame.Stage;

[Alias("stage"), Orleans.Metadata.DefaultGrainType(FlameVocabulary.StageType)]
public interface IStage : INeuron
{
    Task Go(string sceneKey, string? dockSurfaceKey = null);
    Task Back();
    Task Home();
    Task SetHome(string sceneKey);
    [ReadOnly] Task<StageState> Read();
}

[GenerateSerializer, Alias("flame.stage-entry")]
public sealed record StageEntry([property: Id(0)] string SceneKey, [property: Id(1)] string? DockSurfaceKey);

[GenerateSerializer, Alias("flame.stage-state")]
public sealed class StageState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public long Revision { get; set; }
    [Id(2)] public string Home { get; set; } = "";
    [Id(3)] public StageEntry[] Stack { get; set; } = [];
}
