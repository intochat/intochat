using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.Table;

[NeuronAlias("ui.table-source")]
public interface ITableSource
{
    [ReadOnlyCall]
    Task<TablePage> Query(TableQuery query);
}
