using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.InfoBar.Signals;

namespace IntoChat.OS.Modules.UI.InfoBar;

[Neuron("ui.info-bar")]
internal sealed class InfoBarNeuron : UiNeuronBase<InfoBarState>, IInfoBar
{
    public async Task<InfoBarState> Show(string title, string message, string severity = "info", bool isClosable = true)
    {
        var normalized = (severity ?? "info").Trim().ToLowerInvariant() switch
        {
            "success" or "done" => "success",
            "warning" => "warning",
            "error" => "error",
            _ => "info",
        };
        var next = State with
        {
            Version = State.Version + 1,
            IsOpen = true,
            Title = title ?? "",
            Message = message ?? "",
            Severity = normalized,
            IsClosable = isClosable,
        };
        await Save(next, new InfoBarChanged(Id.FullKey(), next.Version, true, normalized));
        return next;
    }

    public async Task<InfoBarState> Dismiss()
    {
        if (!State.IsOpen) { return State; }
        var next = State with { Version = State.Version + 1, IsOpen = false };
        await Save(next, new InfoBarChanged(Id.FullKey(), next.Version, false, next.Severity));
        return next;
    }

    public Task<InfoBarState> Read() => Task.FromResult(State);
}
