namespace IntoChat.OS.Modules.UI.Table;

[GenerateSerializer, Alias("ui.table.column")]
public sealed record TableColumn(
    [property: Id(0)] string Id,
    [property: Id(1)] string Label,
    [property: Id(2)] bool Sortable = true)
{
    public TableColumn() : this("", "", true) { }
}

[GenerateSerializer, Alias("ui.table.query")]
public sealed record TableQuery(
    [property: Id(0)] string Filter = "",
    [property: Id(1)] string? SortBy = null,
    [property: Id(2)] bool Descending = false,
    [property: Id(3)] int Page = 1,
    [property: Id(4)] int PageSize = 10)
{
    public TableQuery() : this("", null, false, 1, 10) { }
}

[GenerateSerializer, Alias("ui.table.page")]
public sealed record TablePage(
    [property: Id(0)] IReadOnlyList<IReadOnlyList<string>> Rows,
    [property: Id(1)] int Total,
    [property: Id(2)] string? Title = null,
    [property: Id(3)] IReadOnlyList<TableColumn>? Columns = null)
{
    public TablePage() : this([], 0, null, null) { }
}

[GenerateSerializer, Alias("ui.table.state")]
public sealed record TableState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Title,
    [property: Id(2)] IReadOnlyList<TableColumn> Columns,
    [property: Id(3)] IReadOnlyList<IReadOnlyList<string>> Rows,
    [property: Id(4)] int Total,
    [property: Id(5)] string Filter,
    [property: Id(6)] string? SortBy,
    [property: Id(7)] bool Descending,
    [property: Id(8)] int Page,
    [property: Id(9)] int PageSize,
    [property: Id(10)] string? SourceNeuronId,
    [property: Id(11)] int? SelectedRowIndex,
    [property: Id(12)] string Status,
    [property: Id(13)] string? Error)
{
    public TableState() : this(0, "", [], [], 0, "", null, false, 1, 10, null, null, "empty", null) { }
}
