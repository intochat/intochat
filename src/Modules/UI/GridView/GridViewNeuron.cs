using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.GridView.Signals;

namespace IntoChat.OS.Modules.UI.GridView;

[Neuron("ui.grid-view")]
internal sealed class GridViewNeuron : UiNeuronBase<GridViewState>, IGridView
{
    public async Task<GridViewState> Configure(string title, IReadOnlyList<GridViewItem> items, string? selectedId = null, int columns = 3)
    {
        var list = items is not null ? [.. items] : Array.Empty<GridViewItem>();
        var next = State with
        {
            Version = State.Version + 1,
            Title = title ?? "Grid",
            Items = list,
            SelectedId = selectedId ?? list.FirstOrDefault()?.Id,
            Columns = Math.Clamp(columns, 1, 6),
        };
        await Save(next, new GridViewChanged(Id.FullKey(), next.Version, next.SelectedId));
        return next;
    }

    public async Task<GridViewState> Select(string itemId)
    {
        if (!State.Items.Any(i => string.Equals(i.Id, itemId, StringComparison.Ordinal))) { return State; }
        if (string.Equals(State.SelectedId, itemId, StringComparison.Ordinal)) { return State; }
        var next = State with { Version = State.Version + 1, SelectedId = itemId };
        await Save(next, new GridViewChanged(Id.FullKey(), next.Version, next.SelectedId));
        return next;
    }

    public Task<GridViewState> Read() => Task.FromResult(State);
}
