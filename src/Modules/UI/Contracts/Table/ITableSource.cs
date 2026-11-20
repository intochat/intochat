using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.Table;

[NeuronAlias("ui.table-source")]
public interface IUiTableSource
{
    [ReadOnlyCall]
    Task<UiTablePage> Query(UiTableQuery query);
}
