using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.CommandBar.Signals;

namespace IntoChat.OS.Modules.UI.CommandBar;

[Neuron("ui.command-bar")]
internal sealed class CommandBarNeuron : UiNeuronBase<CommandBarState>, ICommandBar
{
    public async Task<CommandBarState> Configure(IReadOnlyList<CommandBarItem> items)
    {
        var list = items is not null ? [.. items] : Array.Empty<CommandBarItem>();
        var next = State with { Version = State.Version + 1, Items = list };
        await Save(next, new CommandBarInvoked(Id.FullKey(), next.Version, "", ""));
        return next;
    }

    public async Task<CommandBarState> Invoke(string itemId)
    {
        var item = State.Items.FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.Ordinal) && !i.Disabled);
        if (item is null) { return State; }
        var next = State with { Version = State.Version + 1, LastInvokedId = item.Id, InvokeCount = State.InvokeCount + 1 };
        await Save(next, new CommandBarInvoked(Id.FullKey(), next.Version, item.Id, item.Label));
        return next;
    }

    public Task<CommandBarState> Read() => Task.FromResult(State);
}
