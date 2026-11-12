using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Flame.Rules;
using IntoChat.OS.Modules.Flame.Scene;
using IntoChat.OS.Modules.Flame.Scene.Signals;

namespace IntoChat.OS.Modules.Flame;

[Neuron(FlameVocabulary.SceneType)]
internal sealed class SceneNeuron
    : DigitalBrain.Neurons.Neuron<SceneState>, IScene
{
    public Task Set(SceneDefinition definition, long expectedRevision)
    {
        var name = Id.FullKey();
        SceneAccess.RequireWriter(name);
        SceneValidation.Check(definition);
        if (State.Revision != expectedRevision) { throw new RevisionConflictException($"Scene changed (revision {State.Revision}); read it and set again.", State.Revision); }
        var next = new SceneState { Name = name, Revision = State.Revision + 1, Definition = definition };
        return Save(next, new SceneChanged(next.Name, next.Revision));
    }

    // A report is not a change: it publishes without touching the state or its revision.
    public async Task Report(string nodeId, string kind, double x, double y, string? payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var name = Id.FullKey();
        SceneAccess.RequireReporter(name);
        await Check("Report", new SceneNodeExists(nodeId, Contains(State.Definition.Nodes, nodeId)));
        await Publish(new SceneInput(name, nodeId, kind, x, y, payload));
    }

    private static bool Contains(SceneNode[] nodes, string id) => nodes.Any(n => n.Id == id || Contains(n.Children, id));

    [DigitalBrain.Contracts.ReadOnlyCall] public Task<SceneState> Read() { State.Name = Id.FullKey(); return Task.FromResult(State); }
}
