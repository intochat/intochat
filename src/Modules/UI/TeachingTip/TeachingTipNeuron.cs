using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.TeachingTip.Signals;

namespace IntoChat.OS.Modules.UI.TeachingTip;

[Neuron("ui.teaching-tip")]
internal sealed class TeachingTipNeuron : Neuron<TeachingTipState>, ITeachingTip
{
    public async Task<TeachingTipState> Show(string title, string subtitle, string? actionLabel = null)
    {
        var next = State with
        {
            Version = State.Version + 1,
            Title = title ?? "Tip",
            Subtitle = subtitle ?? "",
            IsOpen = true,
            ActionLabel = actionLabel,
        };
        await Save(next, new TeachingTipChanged(Id.FullKey(), next.Version, true, next.ActionCount));
        return next;
    }

    public async Task<TeachingTipState> InvokeAction()
    {
        var next = State with { Version = State.Version + 1, ActionCount = State.ActionCount + 1, IsOpen = false };
        await Save(next, new TeachingTipChanged(Id.FullKey(), next.Version, false, next.ActionCount));
        return next;
    }

    public async Task<TeachingTipState> Dismiss()
    {
        if (!State.IsOpen) { return State; }
        var next = State with { Version = State.Version + 1, IsOpen = false };
        await Save(next, new TeachingTipChanged(Id.FullKey(), next.Version, false, next.ActionCount));
        return next;
    }

    public Task<TeachingTipState> Read() => Task.FromResult(State);
}
