using IntoChat.OS.Modules.UI.Core;
using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.CommandBar;

[GenerateSerializer, Alias("ui.command-bar.item")]
public sealed record CommandBarItem(
    [property: Id(0)] string Id,
    [property: Id(1)] string Label,
    [property: Id(2)] string? Icon = null,
    [property: Id(3)] bool Primary = true,
    [property: Id(4)] bool Disabled = false)
{
    public CommandBarItem() : this("", "", null, true, false) { }
}

[GenerateSerializer, Alias("ui.command-bar.state")]
public sealed record CommandBarState(
    [property: Id(0)] int Version,
    [property: Id(1)] CommandBarItem[] Items,
    [property: Id(2)] string? LastInvokedId,
    [property: Id(3)] int InvokeCount)
{
    public CommandBarState() : this(0, [], null, 0) { }
}

[NeuronAlias("ui.command-bar")]
[DefaultNeuronType("ui.command-bar")]
public interface ICommandBar : IUiInteractiveControl
{
    Task<CommandBarState> Configure(IReadOnlyList<CommandBarItem> items);
    Task<CommandBarState> Invoke(string itemId);
    [ReadOnlyCall] Task<CommandBarState> Read();
}
