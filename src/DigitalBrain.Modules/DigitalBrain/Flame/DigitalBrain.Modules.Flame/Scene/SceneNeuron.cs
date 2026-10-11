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
        SceneAccess.RequireWriter(this.GetPrimaryKeyString());
        SceneValidation.Check(definition);
        var next = Snapshot;
        if (next.Revision != expectedRevision) { throw new InvalidOperationException($"Scene changed (revision {next.Revision}); read it and set again."); }
        next.Name = this.GetPrimaryKeyString();
        next.Revision++;
        next.Definition = definition;
        next.Current = definition.Machines.ToDictionary(m => m.Id, m => m.Initial, StringComparer.Ordinal);
        return Save(next, new SceneChanged(next.Name, next.Revision));
    }

    public async Task Fire(string eventName, string? nodeId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        var name = this.GetPrimaryKeyString();
        SceneAccess.RequireReporter(name);
        var next = Snapshot;
        var moved = new List<SceneEntered>();
        foreach (var machine in next.Definition.Machines)
        {
            var current = next.Current[machine.Id];
            var transition = machine.Transitions.FirstOrDefault(t => t.From == current && t.Event == eventName);
            if (transition is null) { continue; }
            next.Current[machine.Id] = transition.To;
            moved.Add(new SceneEntered(name, machine.Id, transition.To));
        }
        if (moved.Count == 0) { return; }
        next.Name = name;
        await Save(next, moved[0]);
        foreach (var signal in moved.Skip(1)) { await PublishAsync(signal); }
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
