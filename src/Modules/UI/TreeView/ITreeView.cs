using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.TreeView;

[NeuronAlias("ui.tree-view")]
[DefaultNeuronType("ui.tree-view")]
public interface ITreeView : IUiInteractiveControl
{
    Task<TreeViewState> Configure(IReadOnlyList<TreeNode> nodes, IReadOnlyList<string>? expandedIds = null, string? selectedId = null);
    Task<TreeViewState> ToggleExpand(string nodeId);
    Task<TreeViewState> Select(string nodeId);

    [ReadOnlyCall]
    Task<TreeViewState> Read();
}
