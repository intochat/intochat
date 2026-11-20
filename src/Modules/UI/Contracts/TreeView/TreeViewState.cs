namespace IntoChat.OS.Modules.UI.TreeView;

[GenerateSerializer, Alias("ui.tree-view.node")]
public sealed record TreeNode(
    [property: Id(0)] string Id,
    [property: Id(1)] string Label,
    [property: Id(2)] string? ParentId = null,
    [property: Id(3)] string? Icon = null,
    [property: Id(4)] bool IsFolder = false)
{
    public TreeNode() : this("", "", null, null, false) { }
}

[GenerateSerializer, Alias("ui.tree-view.state")]
public sealed record TreeViewState(
    [property: Id(0)] int Version,
    [property: Id(1)] TreeNode[] Nodes,
    [property: Id(2)] string[] ExpandedIds,
    [property: Id(3)] string? SelectedId)
{
    public TreeViewState() : this(0, [], [], null) { }
}
