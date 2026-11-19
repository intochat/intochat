using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.BreadcrumbBar.Signals;

namespace IntoChat.OS.Modules.UI.BreadcrumbBar;

[Neuron("ui.breadcrumb")]
internal sealed class BreadcrumbBarNeuron : Neuron<BreadcrumbBarState>, IBreadcrumbBar
{
    public async Task<BreadcrumbBarState> SetItems(IReadOnlyList<BreadcrumbItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items.ToArray();
        var active = list.Length > 0 ? list[^1].Id : null;
        var next = State with { Version = State.Version + 1, Items = list, ActiveId = active };
        await Save(next, new BreadcrumbClicked(Id.FullKey(), next.Version, active ?? ""));
        return next;
    }

    public async Task<BreadcrumbBarState> NavigateTo(string itemId)
    {
        var idx = -1;
        for (var i = 0; i < State.Items.Length; i++)
        {
            if (string.Equals(State.Items[i].Id, itemId, StringComparison.Ordinal)) { idx = i; break; }
        }
        if (idx < 0) { throw new ArgumentException($"Unknown breadcrumb '{itemId}'.", nameof(itemId)); }
        var trimmed = State.Items.Take(idx + 1).ToArray();
        var next = State with { Version = State.Version + 1, Items = trimmed, ActiveId = itemId };
        await Save(next, new BreadcrumbClicked(Id.FullKey(), next.Version, itemId));
        return next;
    }

    public Task<BreadcrumbBarState> Read() => Task.FromResult(State);
}
