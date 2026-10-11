using DigitalBrain.Flame.Scene;
using DigitalBrain.Flame.Scene.Signals;
using DigitalBrain.Framework;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace DigitalBrain.Flame;

[GrainType(FlameVocabulary.SceneType)]
internal sealed class SceneNeuron([PersistentState("state", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<SceneState> store)
    : Neuron<SceneState>(store), IScene
{
    public Task Set(SceneDefinition definition, long expectedRevision)
    {
        SceneValidation.Check(definition);
        var next = Snapshot;
        if (next.Revision != expectedRevision) { throw new InvalidOperationException($"Scene changed (revision {next.Revision}); read it and set again."); }
        next.Name = this.GetPrimaryKeyString();
        next.Revision++;
        next.Definition = definition;
        next.Current = definition.Machines.ToDictionary(m => m.Id, m => m.Initial, StringComparer.Ordinal);
        return Save(next, new SceneChanged(next.Name, next.Revision));
    }

    public Task Fire(string eventName, string? nodeId = null) => throw new NotImplementedException();   // Task 3
    public Task Report(string nodeId, string kind, double x, double y, string? payload) => throw new NotImplementedException();   // Task 3

    [ReadOnly] public Task<SceneState> Read() { Snapshot.Name = this.GetPrimaryKeyString(); return Task.FromResult(Snapshot); }
}
