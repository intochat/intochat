/*
# Slider

A control neuron: a value between a minimum and a maximum. Its label, range, step and unit are props its parent writes on the slot. It
owns its value: SetValue stores it and signals ValueChanged, or, when the parent owns the value (props Value), only signals it, every
time. The parent refuses a value outside the props' range (control.outOfRange). The client sends SetValue once, when the person lets go,
and draws its own value while the person drags.
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A slider: it owns its value and signals <see cref="ValueChanged"/>.</summary>
[Neuron("control.slider"), Publishes(typeof(ValueChanged))]
public sealed class SliderNeuron : ControlNeuron<SliderControlState, SliderProps>, ISliderControl
{
    [On]
    public Task SetValue(SetValue command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!double.IsFinite(command.Value)) { throw new ArgumentOutOfRangeException(nameof(command), "A slider's value is a finite number."); }
        if (Props?.Value is not null) { return Publish(new ValueChanged(command.Value)); }
        return command.Value == State.Value ? Task.CompletedTask : Show(State with { Value = command.Value }, new ValueChanged(command.Value));
    }

    protected override View Render(SliderControlState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(Key, new SliderView(Key, state.Value));
    }
}
