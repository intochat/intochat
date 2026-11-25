/*
# Stepper

A control neuron: a row of steps of which one is active. Its title and steps (each with its status) are props its parent writes on the
child node. It owns the active step: Select stores it and signals SelectionChanged, or, when the parent owns the active step (props
ActiveStepId), only signals it, every time. The parent refuses a Select naming no step of its props (control.unknownItem).
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A stepper: it owns the active step and signals <see cref="SelectionChanged"/>.</summary>
[Neuron("control.stepper"), Publishes(typeof(SelectionChanged))]
public sealed class StepperNeuron : ControlNeuron<StepperControlState, StepperProps>, IStepperControl
{
    [On]
    public Task Select(DigitalBrain.UI.Commands.Select command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Id);
        if (Props?.ActiveStepId is not null) { return Publish(new SelectionChanged(command.Id)); }
        return command.Id == State.ActiveStepId ? Task.CompletedTask : Show(State with { ActiveStepId = command.Id }, new SelectionChanged(command.Id));
    }

    protected override View Render(StepperControlState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(Key, new StepperView(Key, state.ActiveStepId));
    }
}
