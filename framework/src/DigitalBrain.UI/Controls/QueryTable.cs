namespace DigitalBrain.UI.Controls;

/// <summary>The state of a query table neuron: its query, never its rows (they are read from the source).</summary>
public sealed record QueryTableState
{
    /// <summary>The rows a page holds.</summary>
    public const int PageSize = 50;

    /// <summary>The filter text; empty matches every row.</summary>
    [Showcase("""{"kind":"queryTableView","key":"people","filter":"","sortBy":"name","descending":false,"page":1,"pageSize":50,"total":3,"loaded":true,"columnIds":["id","name","status"],"rows":[["1","Ada","active"],["2","Grace","away"],["3","Linus","active"]],"error":null,"props":{"title":"People","columns":[{"id":"id","title":"Id"},{"id":"name","title":"Name"},{"id":"status","title":"Status"}],"sourceAddress":"local/control.sampleTableSource/people","isEnabled":true,"automationName":"People"}}""")]
    [Showcase("""{"kind":"queryTableView","key":"broken","filter":"","sortBy":null,"descending":false,"page":1,"pageSize":50,"total":0,"loaded":false,"columnIds":[],"rows":[],"error":"The source did not answer.","props":{"title":"Broken","columns":[{"id":"name","title":"Name"}],"sourceAddress":"local/control.sampleTableSource/none","isEnabled":true,"automationName":"Broken"}}""")]
    public string Filter { get; init; } = "";

    /// <summary>The column id the rows are ordered by; null keeps the source's order.</summary>
    public string? SortBy { get; init; }

    /// <summary>True orders largest first.</summary>
    public bool Descending { get; init; }

    /// <summary>The page shown, from 1.</summary>
    public int Page { get; init; } = 1;
}

/// <summary>
/// What a query table neuron draws: its key, query, and the current page's rows as read from the source (cells in <paramref name="ColumnIds"/>
/// order). The client draws the title and column titles from the slot's props.
/// </summary>
/// <param name="Key">The neuron's key, by which the client finds the control.</param>
/// <param name="Filter">The filter text.</param>
/// <param name="SortBy">The column id the rows are ordered by, or null.</param>
/// <param name="Descending">True orders largest first.</param>
/// <param name="Page">The page shown, from 1.</param>
/// <param name="PageSize">The rows a page holds.</param>
/// <param name="Total">How many rows matched the filter in all.</param>
/// <param name="Loaded">False when no page has been read since the neuron was activated (no rows are stored); a Page command for the
/// current page reads it.</param>
/// <param name="ColumnIds">The column ids the cells of each row follow.</param>
/// <param name="Rows">The current page's rows.</param>
/// <param name="Error">What went wrong reading the source; the client draws it as the table's error line. Null when the read succeeded.</param>
[ViewNode("queryTableView")]
public sealed record QueryTableView(string Key, string Filter, string? SortBy, bool Descending, int Page, int PageSize, int Total, bool Loaded,
    IReadOnlyList<string> ColumnIds, IReadOnlyList<IReadOnlyList<string>> Rows, string? Error) : Node
{
    /// <inheritdoc />
    public bool Equals(QueryTableView? other) => other is not null && Key == other.Key && Filter == other.Filter && SortBy == other.SortBy
        && Descending == other.Descending && Page == other.Page && PageSize == other.PageSize && Total == other.Total && Loaded == other.Loaded
        && Error == other.Error && ColumnIds.SequenceEqual(other.ColumnIds) && Rows.Count == other.Rows.Count
        && Rows.Zip(other.Rows).All(pair => pair.First.SequenceEqual(pair.Second));

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Key, Filter, SortBy, Page, Total, Rows.Count);
}
