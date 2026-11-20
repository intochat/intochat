using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.Table;

[NeuronAlias("ui.table")]
[DefaultNeuronType("ui.table")]
public interface ITable : IUiInteractiveControl
{
    Task<TableState> Bind(string sourceNeuronId, string title, IReadOnlyList<TableColumn> columns, int pageSize = 10);
    Task<TableState> Render(string title, IReadOnlyList<TableColumn> columns, IReadOnlyList<IReadOnlyList<string>> rows);
    Task<TableState> Filter(string text);
    Task<TableState> Sort(string columnId);
    Task<TableState> Page(int number);
    Task<TableState> SelectRow(int? rowIndex);
    Task<TableState> Refresh();

    [ReadOnlyCall]
    Task<TableState> Read();
}
