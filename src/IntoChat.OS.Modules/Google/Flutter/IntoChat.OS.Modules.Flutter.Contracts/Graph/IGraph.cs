using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Graph;

[NeuronAlias("graph"), DefaultNeuronType(UIVocabulary.GraphType)]
public interface IGraph
{
    Task Render(string title, IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges);
    [ReadOnlyCall] Task<GraphState> Read();
}

[GenerateSerializer, Alias("ui.graph-node")]
public sealed record GraphNode([property: Id(0)] string Id, [property: Id(1)] string Label);

[GenerateSerializer, Alias("ui.graph-edge")]
public sealed record GraphEdge([property: Id(0)] string From, [property: Id(1)] string To);

[GenerateSerializer, Alias("ui.graph-state")]
[ExportableState]
public sealed class GraphState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public string Title { get; set; } = "";
    [Id(3)] public List<GraphNode> Nodes { get; set; } = [];
    [Id(4)] public List<GraphEdge> Edges { get; set; } = [];
}
