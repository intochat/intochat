using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Microsoft.Roslyn.Dynamic;

[Signal("roslyn.codebase-tree.updated")]
public sealed record CodebaseTreeUpdated(string NeuronKey, int Version, int NodeCount, string? ActiveQuery) : Signal;

[GenerateSerializer, Alias("roslyn.codebase-tree.node")]
public sealed record CodebaseTreeNode(
    [property: Id(0)] string Id,
    [property: Id(1)] string Label,
    [property: Id(2)] string? ParentId = null,
    [property: Id(3)] string Kind = "file",
    [property: Id(4)] bool IsFolder = false,
    [property: Id(5)] bool ChildrenLoaded = false,
    [property: Id(6)] string? RelativePath = null)
{
    public CodebaseTreeNode() : this("", "", null, "file", false, false, null) { }
}

[GenerateSerializer, Alias("roslyn.codebase-tree.state")]
public sealed record CodebaseTreeState(
    [property: Id(0)] int Version = 0,
    [property: Id(1)] string RootPath = "",
    [property: Id(2)] CodebaseTreeNode[] Nodes = null!,
    [property: Id(3)] string[] ExpandedIds = null!,
    [property: Id(4)] string? ActiveQuery = null)
{
    public CodebaseTreeState() : this(0, "", [], [], null) { }
}

/// <summary>
/// S12a — Lazy Roslyn codebase tree neuron.
/// Loads workspace folders on demand, expands .cs files into C# syntax symbols
/// via Roslyn, and builds focused topic slices (e.g. "auth") on the fly.
/// </summary>
[NeuronAlias("roslyn.codebase-tree")]
[DefaultNeuronType("roslyn.codebase-tree")]
public interface ICodebaseTree
{
    Task<CodebaseTreeState> OpenRoot(string rootPath);
    Task<CodebaseTreeState> ExpandNode(string nodeId);
    Task<CodebaseTreeState> QueryTopic(string topic, int maxMatches = 25);

    [ReadOnlyCall]
    Task<CodebaseTreeState> Read();
}
