using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.UI;

namespace IntoChat.OS.Apps.Data;

internal static class DataStudioViewRenderer
{
    public const string AppOwner = "app.data";

    public static Emit Emits<TSignal>(TSignal signal) where TSignal : Signal
    {
        ArgumentNullException.ThrowIfNull(signal);
        var alias = typeof(TSignal).GetCustomAttribute<SignalAttribute>(false)?.Alias ?? typeof(TSignal).Name;
        return new Emit(AppOwner, alias, JsonSerializer.SerializeToElement(signal));
    }

    public static View Render(DataStudioAppState state) => new(
        "Data Studio",
        new Column(
        [
            new Surface(
                [
                    new Row(
                    [
                        new TagPill("Data Studio · Live Analytics", "accent", "bar-chart-2"),
                        new TagPill(state.ActiveSource, "info", "database"),
                        new TagPill($"Total €{state.Total:N0}", "ok"),
                        new TagPill($"Avg €{state.Average:N0} · Max €{state.Max:N0}", "neutral"),
                    ]) { Gap = 8 },
                    new Text(state.Summary) { Tabular = true },
                    new Text(state.ActiveQuery) { Mono = true, Tone = "muted" },
                    new Row(
                    [
                        new Button("bar", "Bar", Emit: Emits(new DataStudioSwitchChart("bar")), Tone: state.ChartKind == "bar" ? ButtonTone.Accent : ButtonTone.Neutral) { Icon = "bar-chart-2", Small = true },
                        new Button("line", "Line", Emit: Emits(new DataStudioSwitchChart("line")), Tone: state.ChartKind == "line" ? ButtonTone.Accent : ButtonTone.Neutral) { Icon = "trending-up", Small = true },
                        new Button("pie", "Pie", Emit: Emits(new DataStudioSwitchChart("pie")), Tone: state.ChartKind == "pie" ? ButtonTone.Accent : ButtonTone.Neutral) { Icon = "pie-chart", Small = true },
                        .. state.Columns.Take(3).Select(col => (Node)new Button($"group-by-{col}", $"Group by {col}", Emit: Emits(new DataStudioGroupBy(col, "SUM"))) { Icon = "layers", Small = true }),
                        new Button("export-csv", "Export CSV", Emit: Emits(new DataStudioExportCsv())) { Icon = "download", Small = true },
                        new Button("diagram-in-markdraw", "Diagram in Markdraw", Emit: Emits(new DataStudioToMarkdraw("/work/data-lineage.markdraw"))) { Icon = "pen-tool", Small = true },
                    ]) { Gap = 8 },
                ],
                Kind: SurfaceKind.Acrylic,
                Title: state.ChartTitle),
            new Chart(
                state.ChartKind,
                state.ChartTitle,
                state.ChartPoints.Select(pt => new ChartPointNode(pt.Label, pt.Value)).ToArray(),
                SeriesName: "Current",
                SourceNeuronUri: state.SourceNeuronUri,
                From: state.ActiveSource,
                Prefix: "€"),
            new DataTable(
                $"{state.ChartTitle} ({state.Rows.Length} row(s))",
                state.Columns,
                state.Rows.Select(r => (IReadOnlyList<string>)r).ToArray(),
                SourceNeuronUri: state.SourceNeuronUri,
                From: state.ActiveSource),
        ])
        { Gap = 12 });
}
