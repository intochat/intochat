using System.Globalization;
using DigitalBrain.Neurons;

namespace IntoChat.OS.Modules.UI.Table;

/// <summary>Built-in sample <see cref="ITableSource"/> for the UI Gallery and tests.</summary>
[Neuron("ui.sample-table-source")]
internal sealed class SampleTableSource : Neuron, ITableSource
{
    private static readonly (string Span, string Status, int LatencyMs, string Module)[] Spans =
    [
        ("ui.chart.append", "Done", 4, "Modules.UI"),
        ("ui.chart.bind", "Done", 11, "Modules.UI"),
        ("ino.turn", "Working", 142, "Modules.AI"),
        ("synapse.check", "Done", 84, "Creator"),
        ("storage.list", "Done", 6, "Storage"),
        ("vault.probe", "Done", 7, "Identity"),
        ("time.reminder-tick", "Done", 2, "Modules.Time"),
        ("ui.table.query", "Done", 9, "Modules.UI"),
    ];

    public Task<TablePage> Query(TableQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        IEnumerable<(string Span, string Status, int LatencyMs, string Module)> rows = Spans;
        if (!string.IsNullOrWhiteSpace(query.Filter))
        {
            var f = query.Filter.Trim();
            rows = rows.Where(r =>
                r.Span.Contains(f, StringComparison.OrdinalIgnoreCase)
                || r.Status.Contains(f, StringComparison.OrdinalIgnoreCase)
                || r.Module.Contains(f, StringComparison.OrdinalIgnoreCase));
        }

        rows = query.SortBy switch
        {
            "span" => query.Descending ? rows.OrderByDescending(r => r.Span, StringComparer.OrdinalIgnoreCase) : rows.OrderBy(r => r.Span, StringComparer.OrdinalIgnoreCase),
            "status" => query.Descending ? rows.OrderByDescending(r => r.Status, StringComparer.OrdinalIgnoreCase) : rows.OrderBy(r => r.Status, StringComparer.OrdinalIgnoreCase),
            "module" => query.Descending ? rows.OrderByDescending(r => r.Module, StringComparer.OrdinalIgnoreCase) : rows.OrderBy(r => r.Module, StringComparer.OrdinalIgnoreCase),
            _ => query.Descending ? rows.OrderByDescending(r => r.LatencyMs) : rows.OrderBy(r => r.LatencyMs),
        };

        var materialized = rows.ToArray();
        var size = Math.Clamp(query.PageSize, 1, 100);
        var skip = Math.Max(0, query.Page - 1) * size;
        IReadOnlyList<IReadOnlyList<string>> pageRows =
        [
            .. materialized.Skip(skip).Take(size).Select(r => (IReadOnlyList<string>)
            [
                r.Span,
                r.Status,
                string.Create(CultureInfo.InvariantCulture, $"{r.LatencyMs} ms"),
                r.Module,
            ]),
        ];

        return Task.FromResult(new TablePage(pageRows, materialized.Length));
    }
}
