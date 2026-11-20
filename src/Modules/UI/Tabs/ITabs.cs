using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.Tabs;

[NeuronAlias("ui.tabs")]
[DefaultNeuronType("ui.tabs")]
public interface ITabs : IUiInteractiveControl
{
    Task<TabsState> Configure(IReadOnlyList<UiTabItem> items, string? selectedId = null, bool isEnabled = true);
    Task<TabsState> Select(string tabId);

    [ReadOnlyCall]
    Task<TabsState> Read();
}
