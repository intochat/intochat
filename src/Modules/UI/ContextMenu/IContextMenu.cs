using IntoChat.OS.Modules.UI.Core;
using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.ContextMenu;

[GenerateSerializer, Alias("ui.context-menu.item")]
public sealed record ContextMenuItem(
    [property: Id(0)] string Id,
    [property: Id(1)] string Label,
    [property: Id(2)] string? Icon = null,
    [property: Id(3)] string? Shortcut = null,
    [property: Id(4)] bool Destructive = false,
    [property: Id(5)] bool Disabled = false)
{
    public ContextMenuItem() : this("", "", null, null, false, false) { }
}

[GenerateSerializer, Alias("ui.context-menu.state")]
public sealed record ContextMenuState(
    [property: Id(0)] int Version,
    [property: Id(1)] string TriggerLabel,
    [property: Id(2)] ContextMenuItem[] Items,
    [property: Id(3)] string? LastInvokedItemId,
    [property: Id(4)] int InvokeCount)
{
    public ContextMenuState() : this(0, "Actions", [], null, 0) { }
}

[NeuronAlias("ui.context-menu")]
[DefaultNeuronType("ui.context-menu")]
public interface IContextMenu : IUiInteractiveControl
{
    Task<ContextMenuState> Configure(string triggerLabel, IReadOnlyList<ContextMenuItem> items);
    Task<ContextMenuState> Invoke(string itemId);
    [ReadOnlyCall] Task<ContextMenuState> Read();
}
