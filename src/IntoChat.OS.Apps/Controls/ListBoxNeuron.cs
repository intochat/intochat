/*
# List box

A control neuron: a list of items (title and subtitle) of which one may be selected. It owns the selected id and signals SelectionChanged when it changes.
Its items, selection mode, enabled and accessible name are props its parent writes on the slot; the parent refuses commands while disabled and
refuses a Select naming no item of the props (control.unknownItem) or any Select in None mode (control.selectionOff), because both are the parent's.
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;

namespace IntoChat.OS.Apps.Controls;

/// <summary>A list box: it owns the selected item and signals <see cref="SelectionChanged"/>.</summary>
[Neuron("control.listBox"), Emits(typeof(SelectionChanged))]
public sealed class ListBoxNeuron : ControlNeuron<ListBoxState, ListBoxProps>
{
    [On]
    public Task Select(DigitalBrain.UI.Commands.Select command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.Id == State.SelectedId
            ? Task.CompletedTask
            : Show(State with { SelectedId = command.Id }, new SelectionChanged(command.Id));
    }

    protected override View Render(ListBoxState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(Key, new ListBoxView(Key, state.SelectedId));
    }
}
