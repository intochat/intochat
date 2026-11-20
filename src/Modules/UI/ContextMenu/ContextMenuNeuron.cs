using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.ContextMenu.Signals;

namespace IntoChat.OS.Modules.UI.ContextMenu;

[Neuron("ui.context-menu")]
internal sealed class ContextMenuNeuron : UiNeuronBase<ContextMenuState>, IContextMenu
{
    public async Task<ContextMenuState> Configure(string triggerLabel, IReadOnlyList<ContextMenuItem> items)
    {
        var list = items is not null ? [.. items] : Array.Empty<ContextMenuItem>();
        var next = State with
        {
            Version = State.Version + 1,
            TriggerLabel = string.IsNullOrWhiteSpace(triggerLabel) ? "Actions" : triggerLabel.Trim(),
            Items = list,
        };
        await Save(next, new ContextMenuInvoked(Id.FullKey(), next.Version, "", next.TriggerLabel));
        return next;
    }

    public async Task<ContextMenuState> Invoke(string itemId)
    {
        var item = State.Items.FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.Ordinal) && !i.Disabled);
        if (item is null) { return State; }
        var next = State with
        {
            Version = State.Version + 1,
            LastInvokedItemId = item.Id,
            InvokeCount = State.InvokeCount + 1,
        };
        await Save(next, new ContextMenuInvoked(Id.FullKey(), next.Version, item.Id, item.Label));
        return next;
    }

    public Task<ContextMenuState> Read() => Task.FromResult(State);
}
