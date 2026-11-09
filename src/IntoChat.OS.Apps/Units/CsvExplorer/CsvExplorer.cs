using System.Linq;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.UI;

namespace Units.CsvExplorer;

[State("data.csv")]
public sealed record CsvTable(string Title, string[] Columns, string[][] Rows, string Filter)
{
    public CsvTable() : this("CSV explorer", [], [], "") { }
}

public static class CsvExplorerUnit
{
    [Synapse("csv.load", "data.csv")]
    public static CsvTable Load(SignalEnvelope signal, CsvTable table)
    {
        var raw = Read(signal.Payload, "csv");
        if (string.IsNullOrWhiteSpace(raw)) { return table; }
        var lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) { return table; }
        var columns = SplitRow(lines[0]);
        var rows = lines.Skip(1).Select(SplitRow).ToArray();
        var title = Read(signal.Payload, "title")?.Trim();
        return table with
        {
            Title = string.IsNullOrEmpty(title) ? table.Title : title,
            Columns = columns,
            Rows = rows,
        };
    }

    [Synapse("csv.filter", "data.csv")]
    public static CsvTable FilterRows(SignalEnvelope signal, CsvTable table)
    {
        var filter = (Read(signal.Payload, "value") ?? Read(signal.Payload, "filter") ?? "").Trim();
        return table with { Filter = filter };
    }

    [Synapse("data.csv")]
    public static View Show(CsvTable table)
    {
        var matching = MatchingRows(table);
        return new("CSV explorer", new Column(
        [
            new Heading(table.Title, Subtitle: string.IsNullOrEmpty(table.Filter) ? "All rows" : $"Filter: {table.Filter}"),
            Ui.StatCard("matching-rows", "Matching rows", $"{matching.Length}", $"of {table.Rows.Length} total", BadgeTone.Accent),
            new DataTable(table.Title, table.Columns, [.. matching.Select(row => (IReadOnlyList<string>)row)]),
            new Field("filter", "data.csv/filter", FieldKind.Text, "Filter rows", OnSubmit: new Emit("data.csv", "csv.filter")),
        ]) { Gap = 12 });
    }

    private static string[][] MatchingRows(CsvTable table)
        => string.IsNullOrEmpty(table.Filter)
            ? table.Rows
            : [.. table.Rows.Where(row => row.Any(cell => cell.Contains(table.Filter, StringComparison.OrdinalIgnoreCase)))];

    private static string[] SplitRow(string line)
        => [.. line.Split(',').Select(cell => cell.Trim())];

    private static string? Read(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object) { return null; }
        foreach (var property in payload.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
            { return property.Value.GetString(); }
        }
        return null;
    }
}
