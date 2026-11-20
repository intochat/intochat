using IntoChat.OS.Modules.UI.Core;
using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.ListBox.Signals;

namespace IntoChat.OS.Modules.UI.ListBox;

[Neuron("ui.listbox")]
internal sealed class ListBoxNeuron : UiNeuronBase<UiListBoxState>, IListBox
{
    public async Task<UiListBoxState> Configure(IReadOnlyList<UiListBoxItem> items, string? selectedId = null, bool isEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(items);
        var resolved = selectedId ?? State.SelectedId;
        if (resolved is not null && items.All(i => !string.Equals(i.Id, resolved, StringComparison.Ordinal)))
        {
            resolved = items.FirstOrDefault()?.Id;
        }
        var next = State with
        {
            Version = State.Version + 1,
            Items = [.. items],
            SelectedId = resolved,
            IsEnabled = isEnabled,
        };
        await Save(next, new ListBoxChanged(Id.FullKey(), next.Version, next.SelectedId));
        return next;
    }

    public async Task<UiListBoxState> Select(string itemId)
    {
        if (!State.IsEnabled)
        {
            throw new InvalidOperationException($"ListBox '{Id.FullKey()}' is disabled.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        if (State.Items.All(i => !string.Equals(i.Id, itemId, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Item '{itemId}' is not in listbox '{Id.FullKey()}'.", nameof(itemId));
        }
        if (string.Equals(State.SelectedId, itemId, StringComparison.Ordinal)) { return State; }
        var next = State with { Version = State.Version + 1, SelectedId = itemId };
        await Save(next, new ListBoxChanged(Id.FullKey(), next.Version, next.SelectedId));
        return next;
    }

    public Task<UiListBoxState> Read() => Task.FromResult(State);
}
