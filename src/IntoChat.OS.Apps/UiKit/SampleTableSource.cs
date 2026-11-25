/*
# Sample table source

A table source of 120 sample rows (id, name, status) a query table can read by address; the uikit showcase uses it. The filter is a
case-insensitive contains on the name; any column sorts (ordinal; the id numerically).
*/
using System.Globalization;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;

namespace IntoChat.OS.Apps.UiKit;

/// <summary>The sample table source's state: it has none, the rows are fixed.</summary>
public sealed record SampleTableSourceState;

/// <summary>A table source of 120 fixed rows: id, name, status.</summary>
[Neuron("control.sampleTableSource")]
public sealed class SampleTableSource : Neuron<SampleTableSourceState>, ITableSource
{
    private static readonly string[] Statuses = ["active", "away", "offline"];

    private static readonly IReadOnlyList<QueryTableColumn> ColumnList = [new("id", "Id"), new("name", "Name"), new("status", "Status")];

    /// <summary>The 120 rows, id 1 to 120.</summary>
    internal static readonly IReadOnlyList<IReadOnlyDictionary<string, string>> Rows = [.. Enumerable.Range(1, 120).Select(Row)];

    private static IReadOnlyDictionary<string, string> Row(int i) => new Dictionary<string, string>
    {
        ["id"] = i.ToString(CultureInfo.InvariantCulture),
        ["name"] = string.Create(CultureInfo.InvariantCulture, $"Person {i:D3}"),
        ["status"] = Statuses[i % Statuses.Length],
    };

    public Task<IReadOnlyList<QueryTableColumn>> Columns() => Task.FromResult(ColumnList);

    public Task<TablePage> Query(TableQuery q)
    {
        ArgumentNullException.ThrowIfNull(q);
        IEnumerable<IReadOnlyDictionary<string, string>> rows = Rows;
        if (!string.IsNullOrEmpty(q.Filter)) { rows = rows.Where(row => row["name"].Contains(q.Filter, StringComparison.OrdinalIgnoreCase)); }
        if (q.SortBy is { } column && ColumnList.Any(known => known.Id == column))
        {
            rows = column == "id"
                ? (q.Descending ? rows.OrderByDescending(IdOf) : rows.OrderBy(IdOf))
                : (q.Descending ? rows.OrderByDescending(row => row[column], StringComparer.Ordinal) : rows.OrderBy(row => row[column], StringComparer.Ordinal));
        }
        var matched = rows.ToList();
        var size = Math.Max(1, q.PageSize);
        var page = Math.Max(1, q.Page);
        return Task.FromResult(new TablePage([.. matched.Skip((page - 1) * size).Take(size)], matched.Count));
    }

    private static int IdOf(IReadOnlyDictionary<string, string> row) => int.Parse(row["id"], CultureInfo.InvariantCulture);
}
