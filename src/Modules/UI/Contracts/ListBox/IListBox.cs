using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.ListBox;

[NeuronAlias("ui.listbox")]
[DefaultNeuronType("ui.listbox")]
public interface IListBox
{
    Task<ListBoxState> Configure(IReadOnlyList<ListBoxItem> items, string? selectedId = null, bool isEnabled = true);
    Task<ListBoxState> Select(string itemId);

    [ReadOnlyCall]
    Task<ListBoxState> Read();
}
