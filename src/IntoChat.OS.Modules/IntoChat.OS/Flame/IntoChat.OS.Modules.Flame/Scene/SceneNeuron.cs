using IntoChat.OS.Flame.Scene;
using IntoChat.OS.Flame.Scene.Signals;
using IntoChat.OS.Framework;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace IntoChat.OS.Flame;

[GrainType(FlameVocabulary.SceneType)]
internal sealed class SceneNeuron([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<SceneState> store)
    : Neuron<SceneState>(store), IScene
{
    public Task Set(SceneDefinition definition, long expectedRevision)
    {
        var name = this.GetPrimaryKeyString();
        SceneAccess.RequireWriter(name);
        SceneValidation.Check(definition);
        if (Snapshot.Revision != expectedRevision) { throw new RevisionConflictException($"Scene changed (revision {Snapshot.Revision}); read it and set again.", Snapshot.Revision); }
        var next = new SceneState { Name = name, Revision = Snapshot.Revision + 1, Definition = definition };
        return Save(next, new SceneChanged(next.Name, next.Revision));
    }

    // A report is not a change: it publishes without touching the state or its revision.
    public Task Report(string nodeId, string kind, double x, double y, string? payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var name = this.GetPrimaryKeyString();
        SceneAccess.RequireReporter(name);
        if (!Contains(Snapshot.Definition.Nodes, nodeId)) { throw new ArgumentException($"Scene has no node '{nodeId}'.", nameof(nodeId)); }
        return PublishAsync(new SceneInput(name, nodeId, kind, x, y, payload));
    }

    private static bool Contains(SceneNode[] nodes, string id) => nodes.Any(n => n.Id == id || Contains(n.Children, id));

    [ReadOnly] public Task<SceneState> Read() { Snapshot.Name = this.GetPrimaryKeyString(); return Task.FromResult(Snapshot); }
}
