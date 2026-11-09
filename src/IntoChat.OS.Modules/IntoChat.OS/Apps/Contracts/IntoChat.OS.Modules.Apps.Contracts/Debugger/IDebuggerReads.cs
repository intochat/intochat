using DigitalBrain.Contracts;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Trace;

namespace IntoChat.OS.Modules.Apps
{
    // One node of the brain's topology: Role is "unit" for an installed unit's state neuron, "app" for a compiled app, anything
    // else a kernel neuron.
    public sealed record BrainTopologyNode(string Id, string Label, string Role);

    // One reaction or gate: SourceId "*" means any publisher of SignalType (the alias); the journal tail names who published.
    public sealed record BrainTopologyEdge(string SourceId, string TargetId, string SignalType, string Kind);

    public sealed record BrainTopology(IReadOnlyList<BrainTopologyNode> Nodes, IReadOnlyList<BrainTopologyEdge> Edges)
    {
        // Build flows under way; one past `author` whose subject has no installed unit yet is drawn as a ghost.
        public IReadOnlyList<FlowView> Flows { get; init; } = [];
    }
}

namespace IntoChat.OS.Modules.Apps.Debugger
{
    // The Creator's server-side reads of the running brain: a compiled app cannot address a unit's state neuron
    // ({brain}/{alias}/default) nor the journal, so this neuron proxies IStateReader, IViewReader and the signal history. Every
    // call is a read: nothing here changes the brain or runs code (the Creator app runs scenarios itself).
    [NeuronAlias("creator.reads"), DefaultNeuronType("creator.reads")]
    public interface IDebuggerReads
    {
        const string DefaultKey = "creator/reads";

        // The most signals Signals returns.
        const int MaxSignals = 200;

        // How far back from the head Signals looks.
        const int Window = 5000;

        static DigitalBrain.NeuronId Key(string brain) => new(new DigitalBrain.BrainId(brain), "creator.reads", DefaultKey);

        // The unit's current state record as JSON, or null when it has none saved.
        [DigitalBrain.ReadOnlyCall]
        Task<string?> State(string alias);

        // The unit's published view as JSON {alias, generation, view}, or null before its first view.
        [DigitalBrain.ReadOnlyCall]
        Task<string?> View(string alias);

        // The last limit (1..MaxSignals) journaled signals of alias among the last Window entries, oldest first; publisher, when
        // given, narrows them to one neuron. Empty when no loaded unit reacts to or gates on alias.
        [DigitalBrain.ReadOnlyCall]
        Task<TracePage> Signals(string alias, string? publisher, int limit);

        // The installed units (their reactions and gates as edges) and the compiled apps of the brain.
        [DigitalBrain.ReadOnlyCall]
        Task<BrainTopology> Topology();

        // Whether a loaded unit declares the state alias (always true on a host without a synapse catalog).
        [DigitalBrain.ReadOnlyCall]
        Task<bool> Loaded(string alias);

        // The brain journal's highest sequence: where a live tail starts so nothing older is replayed.
        [DigitalBrain.ReadOnlyCall]
        Task<long> Head();
    }
}
