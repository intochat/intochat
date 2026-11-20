using IntoChat.OS.Modules.UI.Core;
using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.Tabs.Signals;

namespace IntoChat.OS.Modules.UI.Tabs;

[Neuron("ui.tabs")]
internal sealed class TabsNeuron : UiNeuronBase<TabsState>, ITabs
{
    public async Task<TabsState> Configure(IReadOnlyList<UiTabItem> items, string? selectedId = null, bool isEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(items);
        var resolved = selectedId ?? State.SelectedId;
        if (resolved is null || items.All(t => !string.Equals(t.Id, resolved, StringComparison.Ordinal)))
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
        await Save(next, new TabChanged(Id.FullKey(), next.Version, next.SelectedId));
        return next;
    }

    public async Task<TabsState> Select(string tabId)
    {
        if (!State.IsEnabled)
        {
            throw new InvalidOperationException($"Tabs '{Id.FullKey()}' is disabled.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        if (State.Items.All(t => !string.Equals(t.Id, tabId, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Tab '{tabId}' is not in tabs '{Id.FullKey()}'.", nameof(tabId));
        }
        if (string.Equals(State.SelectedId, tabId, StringComparison.Ordinal)) { return State; }
        var next = State with { Version = State.Version + 1, SelectedId = tabId };
        await Save(next, new TabChanged(Id.FullKey(), next.Version, next.SelectedId));
        return next;
    }

    public Task<TabsState> Read() => Task.FromResult(State);
}
