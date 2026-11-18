/*
# Expander

A control neuron: a header that shows or hides its content. It owns whether it is expanded and signals Expanded when that changes.
Its content is data nodes or slots of child neurons, kept in its state.
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;

namespace IntoChat.OS.Apps.Controls;

/// <summary>An expander: it owns whether its content is shown and signals <see cref="Expanded"/>.</summary>
[Neuron("control.expander"), Emits(typeof(Expanded))]
public sealed class ExpanderNeuron : Screen<ExpanderState>
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
        var key = ControlKeys.Of(Id);
        return new(state.Header, new ExpanderView(key, state.Header, state.IsExpanded, state.AutomationName ?? key, state.Children));
    }
}
