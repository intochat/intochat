/*
# Expander

A control neuron: a header that shows or hides its content. It owns whether it is expanded and signals Expanded when that changes.
Its header, content, enabled and accessible name are props its parent writes on the slot; the parent refuses commands while disabled.
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;

namespace IntoChat.OS.Apps.Controls;

/// <summary>An expander: it owns whether its content is shown and signals <see cref="Expanded"/>.</summary>
[Neuron("control.expander"), Emits(typeof(Expanded))]
public sealed class ExpanderNeuron : ControlNeuron<ExpanderState, ExpanderProps>
{
    [On]
    public Task Expand(Expand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.IsExpanded == State.IsExpanded
            ? Task.CompletedTask
            : Show(State with { IsExpanded = command.IsExpanded }, new Expanded(command.IsExpanded));
    }

    protected override View Render(ExpanderState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(Key, new ExpanderView(Key, state.IsExpanded));
    }
}
