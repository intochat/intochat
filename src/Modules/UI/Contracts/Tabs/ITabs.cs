using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.Tabs;

[NeuronAlias("ui.tabs")]
[DefaultNeuronType("ui.tabs")]
public interface ITabs
{
    Task<TabsState> Configure(IReadOnlyList<TabItem> items, string? selectedId = null, bool isEnabled = true);
    Task<TabsState> Select(string tabId);

    [ReadOnlyCall]
    Task<TabsState> Read();
}
