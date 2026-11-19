using System.ComponentModel;
using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.AI.Agents.Context;

[GenerateSerializer, Alias("ui.introspector.method")]
public sealed record UiContractMethod
{
    [Id(0)] public string Name { get; init; } = "";
    [Id(1)] public string[] Parameters { get; init; } = [];
    [Id(2)] public string ReturnType { get; init; } = "";
    [Id(3)] public bool IsReadOnly { get; init; }

    public UiContractMethod() { }

    public UiContractMethod(string name, string[] parameters, string returnType, bool isReadOnly)
    {
        Name = name;
        Parameters = parameters ?? [];
        ReturnType = returnType;
        IsReadOnly = isReadOnly;
    }
}

[GenerateSerializer, Alias("ui.introspector.contract")]
public sealed record UiContractSchema
{
    [Id(0)] public string NeuronKind { get; init; } = "";
    [Id(1)] public string ContractType { get; init; } = "";
    [Id(2)] public string? StateType { get; init; }
    [Id(3)] public UiContractMethod[] Methods { get; init; } = [];
    [Id(4)] public string[] Signals { get; init; } = [];
}

[GenerateSerializer, Alias("ui.introspector.neuron")]
public sealed record UiNeuronSnapshot
{
    [Id(0)] public string NeuronKey { get; init; } = "";
    [Id(1)] public string NeuronKind { get; init; } = "";
    [Id(2)] public string Key { get; init; } = "";
    [Id(3)] public string ContractType { get; init; } = "";
    [Id(4)] public string StateJson { get; init; } = "{}";
    [Id(5)] public string Summary { get; init; } = "";
}

[GenerateSerializer, Alias("ui.introspector.graph-node")]
public sealed record UiStateGraphNode
{
    [Id(0)] public string Id { get; init; } = "";
    [Id(1)] public int Depth { get; init; }
    [Id(2)] public string Label { get; init; } = "";

    public UiStateGraphNode() { }

    public UiStateGraphNode(string id, int depth, string label)
    {
        Id = id;
        Depth = depth;
        Label = label;
    }
}

[GenerateSerializer, Alias("ui.introspector.graph-edge")]
public sealed record UiStateGraphEdge
{
    [Id(0)] public string FromId { get; init; } = "";
    [Id(1)] public string ToId { get; init; } = "";
    [Id(2)] public string Command { get; init; } = "";
    [Id(3)] public bool Accepted { get; init; }
    [Id(4)] public string[] Signals { get; init; } = [];
    [Id(5)] public string[] BrokenRules { get; init; } = [];
    [Id(6)] public string[] Messages { get; init; } = [];
}

[GenerateSerializer, Alias("ui.introspector.state-graph")]
public sealed record UiStateGraphSnapshot
{
    [Id(0)] public string Name { get; init; } = "";
    [Id(1)] public string TypeName { get; init; } = "";
    [Id(2)] public bool IsHealthy { get; init; } = true;
    [Id(3)] public bool Truncated { get; init; }
    [Id(4)] public UiStateGraphNode[] States { get; init; } = [];
    [Id(5)] public UiStateGraphEdge[] Transitions { get; init; } = [];
    [Id(6)] public string[] Violations { get; init; } = [];
    [Id(7)] public string Dot { get; init; } = "";
}

[GenerateSerializer, Alias("ui.introspector.report")]
public sealed record UiIntrospectionReport
{
    [Id(0)] public long Revision { get; init; }
    [Id(1)] public UiContractSchema[] Contracts { get; init; } = [];
    [Id(2)] public UiNeuronSnapshot[] LiveNeurons { get; init; } = [];
    [Id(3)] public UiStateGraphSnapshot[] StateGraphs { get; init; } = [];
    [Id(4)] public string ContextSummary { get; init; } = "";
}

[DigitalBrain.Signal("ui.introspector.synced")]
public sealed record UiIntrospectorSynced(
    string IntrospectorId,
    long Revision,
    int LiveNeuronCount,
    int StateGraphCount) : DigitalBrain.Signal;

[NeuronAlias(NeuronType), DefaultNeuronType(NeuronType)]
[Description("Introspects live Modules.UI neuron states, component contracts, and Accordant StateGraph state/transition graphs, projecting UI awareness into IAgentContext.")]
public interface IUiIntrospector
{
    const string NeuronType = "ui.introspector";
    const string DefaultKey = "default";
    const string DefaultContextKey = "context/ui.state";

    static NeuronId NeuronIdOf(BrainId brain, string key = DefaultKey) => new(brain, NeuronType, key);

    static string Key(BrainId brain, string key = DefaultKey) => NeuronIdOf(brain, key).FullKey();

    [Description("List compiled Modules.UI component contracts, state types, callable methods, and emitted signals.")]
    [ReadOnlyCall, Interleaves]
    Task<UiContractSchema[]> Contracts(string? kindFilter = null);

    [Description("Inspect the live state JSON and summary of a specific Modules.UI neuron in this brain.")]
    [ReadOnlyCall, Interleaves]
    Task<UiNeuronSnapshot?> InspectNeuron(string neuronKind, string key);

    [Description("Inspect all active or tracked Modules.UI neurons in this brain.")]
    [ReadOnlyCall, Interleaves]
    Task<UiNeuronSnapshot[]> InspectLiveNeurons(string? kindFilter = null);

    [Description("Register a Modules.UI neuron instance (kind and key) for continuous UI context introspection.")]
    Task TrackNeuron(string neuronKind, string key);

    [Description("Explore an Aggregate state record or AggregateScreen type via Accordant AggregateExplorer, returning states, rule-checked transitions, violations, and Graphviz DOT.")]
    Task<UiStateGraphSnapshot> ExploreStateGraph(string stateOrScreenTypeName, int maxStates = 32);

    [Description("Publish an unfolded Accordant StateGraph snapshot into the introspector.")]
    Task PublishStateGraph(UiStateGraphSnapshot graph);

    [ReadOnlyCall, Interleaves]
    Task<UiStateGraphSnapshot[]> ListStateGraphs();

    [Description("Synchronize a compact summary of live Modules.UI states, contracts, and Accordant StateGraphs into an IAgentContext slice (defaults to context/ui.state).")]
    Task<UiIntrospectionReport> SyncContext(string contextKey = DefaultContextKey);
}
