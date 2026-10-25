using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Table;

[NeuronAlias("table"), DefaultNeuronType(UIVocabulary.TableType)]
public interface ITable
{
    Task Replace(string title, IReadOnlyList<TableColumn> columns, IReadOnlyList<IReadOnlyList<string>> rows);
    [DigitalBrain.ReadOnlyCall] Task<TableState> Read();
}

[GenerateSerializer, Alias("ui.table-column")]
public sealed record TableColumn([property: Id(0)] string Id, [property: Id(1)] string Title);

[GenerateSerializer, Alias("ui.table-state")]
[ExportableState]
public sealed class TableState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public string Title { get; set; } = "";
    [Id(3)] public List<TableColumn> Columns { get; set; } = [];
    [Id(4)] public List<List<string>> Rows { get; set; } = [];
}
