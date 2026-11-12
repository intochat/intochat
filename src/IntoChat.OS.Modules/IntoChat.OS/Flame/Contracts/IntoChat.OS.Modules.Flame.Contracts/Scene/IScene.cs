using DigitalBrain.Contracts;
using IntoChat.OS;

namespace IntoChat.OS.Modules.Flame.Scene;

[NeuronAlias("scene"), DefaultNeuronType(FlameVocabulary.SceneType)]
public interface IScene
{
    static string Key(string brainScope, string app, string name) => brainScope + "/apps/" + app + "/scenes/" + name;
    Task Set(SceneDefinition definition, long expectedRevision);
    // The shell reports a person's gesture on a node; the neuron publishes it as SceneInput.
    Task Report(string nodeId, string kind, double x, double y, string? payload);
    [DigitalBrain.Contracts.ReadOnlyCall] Task<SceneState> Read();
}
