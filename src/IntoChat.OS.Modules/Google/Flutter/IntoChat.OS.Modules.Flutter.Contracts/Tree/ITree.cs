using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Tree;

[NeuronAlias("tree"), DefaultNeuronType(UIVocabulary.TreeType)]
public interface ITree
{
    Task Set(IReadOnlyList<TreeNode> nodes);
    Task Select(string id);
    [DigitalBrain.ReadOnlyCall] Task<TreeState> Read();
}

[GenerateSerializer, Alias("ui.tree-node")]
public sealed record TreeNode(
    [property: Id(0)] string Id,
    [property: Id(1)] string? ParentId,
    [property: Id(2)] string Label);

[GenerateSerializer, Alias("ui.tree-state")]
[ExportableState]
public sealed class TreeState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public List<TreeNode> Nodes { get; set; } = [];
    [Id(3)] public string SelectedId { get; set; } = "";
}
