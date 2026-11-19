using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.ListBox;

[NeuronAlias("ui.listbox")]
[DefaultNeuronType("ui.listbox")]
public interface IListBox
{
    Task<UiListBoxState> Configure(IReadOnlyList<UiListBoxItem> items, string? selectedId = null, bool isEnabled = true);
    Task<UiListBoxState> Select(string itemId);

    [ReadOnlyCall]
    Task<UiListBoxState> Read();
}
