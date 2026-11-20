using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.TimePicker.Signals;

namespace IntoChat.OS.Modules.UI.TimePicker;

[Neuron("ui.time-picker")]
internal sealed class TimePickerNeuron : UiNeuronBase<TimePickerState>, ITimePicker
{
    public async Task<TimePickerState> Configure(string label, int hour, int minute, bool use24Hour = true, bool disabled = false)
    {
        var h = Math.Clamp(hour, 0, 23);
        var m = Math.Clamp(minute, 0, 59);
        var next = State with
        {
            Version = State.Version + 1,
            Label = label ?? "",
            Hour = h,
            Minute = m,
            Use24Hour = use24Hour,
            Disabled = disabled,
        };
        await Save(next, new TimePickerChanged(Id.FullKey(), next.Version, next.Hour, next.Minute));
        return next;
    }

    public async Task<TimePickerState> SetTime(int hour, int minute)
    {
        if (State.Disabled) { return State; }
        var h = Math.Clamp(hour, 0, 23);
        var m = Math.Clamp(minute, 0, 59);
        if (h == State.Hour && m == State.Minute) { return State; }
        var next = State with { Version = State.Version + 1, Hour = h, Minute = m };
        await Save(next, new TimePickerChanged(Id.FullKey(), next.Version, next.Hour, next.Minute));
        return next;
    }

    public Task<TimePickerState> Read() => Task.FromResult(State);
}
