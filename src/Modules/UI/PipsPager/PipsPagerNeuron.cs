using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.PipsPager.Signals;

namespace IntoChat.OS.Modules.UI.PipsPager;

[Neuron("ui.pips-pager")]
internal sealed class PipsPagerNeuron : Neuron<PipsPagerState>, IPipsPager
{
    public async Task<PipsPagerState> Configure(int pageCount, int selectedIndex = 0, int maxVisiblePips = 5, string orientation = "horizontal", bool showPrevNext = true)
    {
        var count = Math.Max(1, pageCount);
        var visible = Math.Max(1, maxVisiblePips);
        var idx = Math.Clamp(selectedIndex, 0, count - 1);
        var normOrientation = string.Equals(orientation?.Trim(), "vertical", StringComparison.OrdinalIgnoreCase) ? "vertical" : "horizontal";

        var next = State with
        {
            Version = State.Version + 1,
            PageCount = count,
            SelectedIndex = idx,
            MaxVisiblePips = visible,
            Orientation = normOrientation,
            ShowPrevNext = showPrevNext,
        };
        await Save(next, new PipsPagerChanged(Id.FullKey(), next.Version, next.SelectedIndex, next.PageCount));
        return next;
    }

    public async Task<PipsPagerState> SelectPage(int index)
    {
        var count = Math.Max(1, State.PageCount);
        var clamped = Math.Clamp(index, 0, count - 1);
        var next = State with
        {
            Version = State.Version + 1,
            PageCount = count,
            SelectedIndex = clamped,
        };
        await Save(next, new PipsPagerChanged(Id.FullKey(), next.Version, next.SelectedIndex, next.PageCount));
        return next;
    }

    public Task<PipsPagerState> Next() => SelectPage(State.SelectedIndex + 1);

    public Task<PipsPagerState> Previous() => SelectPage(State.SelectedIndex - 1);

    public Task<PipsPagerState> Read() => Task.FromResult(State);
}
