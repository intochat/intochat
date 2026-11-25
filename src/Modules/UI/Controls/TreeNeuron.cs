/*
# Tree

A control neuron: nodes nested by parent id, whose folders open and close. Its nodes are props its parent writes on the child node. It owns
the selected node and the expanded folders, and either can be the parent's instead (props SelectedId, ExpandedIds), so a parent can
open the tree to the node it selected. Owned by the tree, Select and ExpandNode store the change and signal SelectionChanged and
NodeExpanded, and a command that changes nothing signals nothing; owned by the parent, they only signal, every time, and the tree
stores nothing. The parent refuses a node its props do not hold (control.unknownItem) and an ExpandNode of a node that is no folder
and holds no node (control.notAFolder).
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using TreeViewNode = DigitalBrain.UI.Controls.TreeView;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A tree: it owns the selected node and the expanded folders and signals <see cref="SelectionChanged"/> and <see cref="NodeExpanded"/>.</summary>
[Neuron("control.tree"), Publishes(typeof(SelectionChanged)), Publishes(typeof(NodeExpanded))]
public sealed class TreeNeuron : ControlNeuron<TreeState, TreeProps>, ITreeControl
{
    [On]
    public Task Select(DigitalBrain.UI.Commands.Select command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Id);
        if (Props?.SelectedId is not null) { return Publish(new SelectionChanged(command.Id)); }
        return command.Id == State.SelectedId ? Task.CompletedTask : Show(State with { SelectedId = command.Id }, new SelectionChanged(command.Id));
    }

    [On]
    public Task ExpandNode(ExpandNode command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Id);
        var expanded = new NodeExpanded(command.Id, command.IsExpanded);
        if (Props?.ExpandedIds is not null) { return Publish(expanded); }
        if (State.ExpandedIds.Contains(command.Id, StringComparer.Ordinal) == command.IsExpanded) { return Task.CompletedTask; }
        IReadOnlyList<string> next = command.IsExpanded ? [.. State.ExpandedIds, command.Id] : [.. State.ExpandedIds.Where(id => id != command.Id)];
        return Show(State with { ExpandedIds = next }, expanded);
    }

    protected override View Render(TreeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(Key, new TreeViewNode(Key, state.SelectedId, state.ExpandedIds));
    }
}
