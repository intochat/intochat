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
        var name = this.GetPrimaryKeyString();
        SceneAccess.RequireWriter(name);
        SceneValidation.Check(definition);
        if (Snapshot.Revision != expectedRevision) { throw new RevisionConflictException($"Scene changed (revision {Snapshot.Revision}); read it and set again.", Snapshot.Revision); }
        var next = new SceneState
        {
            Name = name,
            Revision = Snapshot.Revision + 1,
            Definition = definition,
            Current = definition.Machines.ToDictionary(m => m.Id, Keep, StringComparer.Ordinal),
        };
        return Save(next, new SceneChanged(next.Name, next.Revision));

        // A recompose keeps a machine where it is while the new definition still has that state; otherwise it starts over.
        string Keep(StateMachine machine)
            => Snapshot.Current.TryGetValue(machine.Id, out var current) && machine.States.Any(state => state.Name == current) ? current : machine.Initial;
    }

    public async Task Fire(string eventName, string? nodeId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        var name = this.GetPrimaryKeyString();
        SceneAccess.RequireReporter(name);
        // Snapshot is the live stored state: the move is built on a new state so a failed save leaves it unmoved.
        var current = new Dictionary<string, string>(Snapshot.Current, StringComparer.Ordinal);
        var moved = new List<SceneEntered>();
        foreach (var machine in Snapshot.Definition.Machines)
        {
            var from = current[machine.Id];
            var transition = machine.Transitions.FirstOrDefault(t => t.From == from && t.Event == eventName);
            if (transition is null) { continue; }
            current[machine.Id] = transition.To;
            moved.Add(new SceneEntered(name, machine.Id, transition.To));
        }
        if (moved.Count == 0) { return; }
        await Save(new SceneState { Name = name, Revision = Snapshot.Revision, Definition = Snapshot.Definition, Current = current }, moved[0]);
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
