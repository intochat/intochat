/*
# Tab strip

A control neuron: a row of tabs of which one is selected. It owns the selected id and signals SelectionChanged when it changes.
Its tabs, enabled and accessible name are props its parent writes on the slot; the parent refuses commands while disabled and
refuses a Select naming no tab of the props (control.unknownItem), because the tabs are the parent's.
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A tab strip: it owns the selected tab and signals <see cref="SelectionChanged"/>.</summary>
[Neuron("control.tabStrip"), Emits(typeof(SelectionChanged))]
public sealed class TabStripNeuron : ControlNeuron<TabStripState, TabStripProps>, ITabStripControl
{
    [On]
    public Task Select(DigitalBrain.UI.Commands.Select command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SelectId(Props?.SelectedId, State.SelectedId, command.Id, id => State with { SelectedId = id });
    }

    protected override View Render(TabStripState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(Key, new TabStripView(Key, state.SelectedId));
    }
}
