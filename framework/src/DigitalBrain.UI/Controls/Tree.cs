namespace DigitalBrain.UI.Controls;

/// <summary>The state of a tree neuron: only what the user changes, the selected node and the expanded folders. Its nodes are the parent's props.</summary>
public sealed record TreeState
{
    /// <summary>The selected node's id; null selects none. Stays null while the parent owns the selection.</summary>
    [Showcase("""{"kind":"treeView","key":"files","selectedId":"app","expandedIds":["src"],"props":{"nodes":[{"id":"src","label":"src","isFolder":true},{"id":"app","label":"app.cs","parentId":"src"},{"id":"lib","label":"lib","parentId":"src","isFolder":true},{"id":"docs","label":"docs","isFolder":true},{"id":"readme","label":"README.md"}],"isEnabled":true,"automationName":"Files"}}""")]
    public string? SelectedId { get; init; }

    /// <summary>The ids of the expanded folders, in the order they were expanded. Stays empty while the parent owns them.</summary>
    [Showcase("""{"kind":"treeView","key":"opened","selectedId":null,"expandedIds":[],"props":{"nodes":[{"id":"src","label":"src","isFolder":true},{"id":"app","label":"app.cs","parentId":"src"}],"isEnabled":true,"automationName":"Opened files","selectedId":"app","expandedIds":["src"]}}""")]
    public IReadOnlyList<string> ExpandedIds { get; init; } = [];
}

/// <summary>What a tree neuron draws: its key, selected node and expanded folders. The client draws the nodes from the child's props.</summary>
/// <param name="Key">The neuron's key, by which the client finds the control.</param>
/// <param name="SelectedId">The tree's own selected node; null when none is selected or the parent owns the selection.</param>
/// <param name="ExpandedIds">The tree's own expanded folders; empty when none is expanded or the parent owns them.</param>
[ViewNode("treeView")]
public sealed record TreeView(string Key, string? SelectedId, IReadOnlyList<string> ExpandedIds) : Node
{
    /// <inheritdoc />
    public bool Equals(TreeView? other) => other is not null && Key == other.Key && SelectedId == other.SelectedId && ExpandedIds.SequenceEqual(other.ExpandedIds);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Key, SelectedId, SequenceHash.Of(ExpandedIds));
}
