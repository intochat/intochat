using System.ComponentModel;
using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.AI.Agents.Context;

// A keyed node a screen draws, as the screen directory lists it: a child's node carries the child key path ("panel/save").
[GenerateSerializer, Alias("ui.introspector.screen-node")]
public sealed record UiScreenNode(
    [property: Id(0)] string Key,
    [property: Id(1)] string Kind,
    [property: Id(2)] string Label,
    [property: Id(3)] string? Value = null);

// One screen as the screen directory reads it, its children drawn in place; ViewJson is bounded.
[GenerateSerializer, Alias("ui.introspector.screen")]
public sealed record UiScreenSnapshot
{
    [Id(0)] public string AppId { get; init; } = "";
    [Id(1)] public string Title { get; init; } = "";
    [Id(2)] public string Kind { get; init; } = "";
    [Id(3)] public long Generation { get; init; }
    [Id(4)] public UiScreenNode[] Nodes { get; init; } = [];
    [Id(5)] public string ViewJson { get; init; } = "";
    [Id(6)] public string Summary { get; init; } = "";
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
    [Id(3)] public UiStateGraphSnapshot[] StateGraphs { get; init; } = [];
    [Id(4)] public string ContextSummary { get; init; } = "";
    [Id(5)] public UiScreenSnapshot[] Screens { get; init; } = [];
}

[DigitalBrain.Signal("ui.introspector.synced")]
public sealed record UiIntrospectorSynced(
    string IntrospectorId,
    long Revision,
    int ScreenCount,
    int StateGraphCount) : DigitalBrain.Signal;

[NeuronAlias(NeuronType), DefaultNeuronType(NeuronType)]
[Description("Reads the screens of this brain through the screen directory, children included, and Accordant StateGraph state/transition graphs, projecting UI awareness into IAgentContext.")]
public interface IUiIntrospector
{
    const string NeuronType = "ui.introspector";
    const string DefaultKey = "default";
    const string DefaultContextKey = "context/ui.state";

    static NeuronId NeuronIdOf(BrainId brain, string key = DefaultKey) => new(brain, NeuronType, key);

    static string Key(BrainId brain, string key = DefaultKey) => NeuronIdOf(brain, key).FullKey();

    [Description("Read one screen of this brain by app name (for example \"tasks\") through the screen directory: its title, generation, the keyed nodes it draws (a child's nodes under the child key path, \"panel/save\") and its view JSON. Null when no screen answers to the name.")]
    [ReadOnlyCall, Interleaves]
    Task<UiScreenSnapshot?> InspectScreen(string appId);

    [Description("Read every screen open in this brain's workspace through the screen directory, children included.")]
    [ReadOnlyCall, Interleaves]
    Task<UiScreenSnapshot[]> InspectOpenScreens();

    [Description("Explore an Aggregate state record or AggregateScreen type via Accordant AggregateExplorer, returning states, rule-checked transitions, violations, and Graphviz DOT.")]
    Task<UiStateGraphSnapshot> ExploreStateGraph(string stateOrScreenTypeName, int maxStates = 32);

    [Description("Publish an unfolded Accordant StateGraph snapshot into the introspector.")]
    Task PublishStateGraph(UiStateGraphSnapshot graph);

    [ReadOnlyCall, Interleaves]
    Task<UiStateGraphSnapshot[]> ListStateGraphs();

    [Description("Synchronize a compact summary of the open screens and Accordant StateGraphs into an IAgentContext slice (defaults to context/ui.state).")]
    Task<UiIntrospectionReport> SyncContext(string contextKey = DefaultContextKey);
}
