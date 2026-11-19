/*
# Combo box

A control neuron: a drop-down of options of which one is selected. It owns the selected id and signals SelectionChanged when it changes.
Its header, options, enabled and accessible name are props its parent writes on the slot; the parent refuses commands while disabled and
refuses a Select naming no option of the props (control.unknownItem), because the options are the parent's.
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;

namespace IntoChat.OS.Apps.Controls;

/// <summary>A combo box: it owns the selected option and signals <see cref="SelectionChanged"/>.</summary>
[Neuron("control.comboBox"), Emits(typeof(SelectionChanged))]
public sealed class ComboBoxNeuron : ControlNeuron<ComboBoxState, ComboBoxProps>
{
    [On]
    public Task Select(DigitalBrain.UI.Commands.Select command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SelectId(Props?.SelectedId, State.SelectedId, command.Id, id => State with { SelectedId = id });
    }

    protected override View Render(ComboBoxState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(Key, new ComboBoxView(Key, state.SelectedId));
    }
}
