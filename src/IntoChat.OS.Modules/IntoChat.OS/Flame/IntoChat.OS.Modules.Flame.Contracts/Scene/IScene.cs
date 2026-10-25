using IntoChat.OS;
using Orleans.Concurrency;

namespace IntoChat.OS.Modules.Flame.Scene;

[Alias("scene"), Orleans.Metadata.DefaultGrainType(FlameVocabulary.SceneType)]
public interface IScene : INeuron
{
    static string Key(string brainScope, string app, string name) => brainScope + "/apps/" + app + "/scenes/" + name;
    Task Set(SceneDefinition definition, long expectedRevision);
    // The shell reports a person's gesture on a node; the neuron publishes it as SceneInput.
    Task Report(string nodeId, string kind, double x, double y, string? payload);
    [ReadOnly] Task<SceneState> Read();
}
