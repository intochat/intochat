using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.TreeView.Signals;

namespace IntoChat.OS.Modules.UI.TreeView;

[Neuron("ui.tree-view")]
internal sealed class TreeViewNeuron : UiNeuronBase<TreeViewState>, ITreeView
{
    public async Task<TreeViewState> Configure(IReadOnlyList<TreeNode> nodes, IReadOnlyList<string>? expandedIds = null, string? selectedId = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var next = State with
        {
            Version = State.Version + 1,
            Nodes = [.. nodes],
            ExpandedIds = expandedIds is not null ? [.. expandedIds] : State.ExpandedIds,
            SelectedId = selectedId ?? State.SelectedId,
        };
        await Save(next, new TreeViewChanged(Id.FullKey(), next.Version, next.SelectedId));
        return next;
    }

    public async Task<TreeViewState> ToggleExpand(string nodeId)
    {
        var set = State.ExpandedIds.ToHashSet(StringComparer.Ordinal);
        if (!set.Remove(nodeId)) { set.Add(nodeId); }
        var next = State with { Version = State.Version + 1, ExpandedIds = [.. set] };
        await Save(next, new TreeViewChanged(Id.FullKey(), next.Version, next.SelectedId));
        return next;
    }

    public async Task<TreeViewState> Select(string nodeId)
    {
        if (State.Nodes.All(n => !string.Equals(n.Id, nodeId, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Unknown tree node '{nodeId}'.", nameof(nodeId));
        }
        var next = State with { Version = State.Version + 1, SelectedId = nodeId };
        await Save(next, new TreeViewChanged(Id.FullKey(), next.Version, next.SelectedId));
        return next;
    }

    public Task<TreeViewState> Read() => Task.FromResult(State);
}
