using DigitalBrain.UI;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Data;

internal static class DataStudioViewRenderer
{
    public const string AppOwner = "app.data";

    public static Node RenderNode(DataStudioAppState state)
    {
        var hasRows = state.Rows.Length > 0;
        var distinctColumns = state.Columns.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToArray();

        Node root = VStack(Space.Md)[
            HeaderBar(
                string.IsNullOrWhiteSpace(state.ChartTitle) ? "Data Studio" : state.ChartTitle,
                string.IsNullOrWhiteSpace(state.ActiveSource) ? "SQL · CSV · JSON Analytics" : $"Source: {state.ActiveSource}",
                StatusPill(hasRows ? StatusKind.Done : StatusKind.Idle, hasRows ? $"{state.Rows.Length} rows" : "No dataset"),
                Button("Export CSV", "download")
                    .Key("export-csv")
                    .Small()
                    .OnClickAsync<DataStudioApp>(s => s.ExportCsvDefaultAsync())
                    .Guard(state.HasDatasetRows()),
                PrimaryButton("Diagram in Markdraw", "pen-tool")
                    .Key("diagram-in-markdraw")
                    .Small()
                    .OnClickAsync<DataStudioApp>(s => s.DiagramInMarkdrawDefaultAsync())
                    .Guard(state.HasDatasetRows())),
            Grid(4, Space.Sm)[
                StatCard("kpi-data-total", "Total", $"€{state.Total:N0}", hasRows ? $"{state.Rows.Length} rows" : "Idle", BadgeTone.Accent, "bar-chart-2"),
                StatCard("kpi-data-avg", "Average", $"€{state.Average:N0}", "Mean", BadgeTone.Done, "trending-up"),
                StatCard("kpi-data-min", "Minimum", $"€{state.Min:N0}", "Floor", BadgeTone.Neutral, "arrow-down"),
                StatCard("kpi-data-max", "Maximum", $"€{state.Max:N0}", "Peak", BadgeTone.Neutral, "arrow-up")
            ],
            Card("Query & Visualization", state.Summary)[
                VStack(Space.Sm)[
                    string.IsNullOrWhiteSpace(state.ActiveQuery)
                        ? MutedText("Open a dataset or run a SQL query to inspect aggregates.")
                        : Text(state.ActiveQuery).Muted().Tabular(),
                    HStack(Space.Sm,
                    [
                        Button("Bar", "bar-chart-2").Key("bar").Small().OnClick<DataStudioAppState>(x => x.SwitchChart("bar")),
                        Button("Line", "trending-up").Key("line").Small().OnClick<DataStudioAppState>(x => x.SwitchChart("line")),
                        Button("Pie", "pie-chart").Key("pie").Small().OnClick<DataStudioAppState>(x => x.SwitchChart("pie")),
                        .. distinctColumns.Select(col =>
                            GhostButton($"Group by {col}", "layers")
                                .Key($"group-by-{col}")
                                .Small()
                                .OnClickAsync<DataStudioApp>(s => s.GroupByColumnAsync(col))
                                .ToNode()),
                    ])
                ]
            ],
            state.ChartPoints.Length > 0
                ? new Chart(
                    state.ChartKind == "bar" ? "columns" : state.ChartKind,
                    state.ChartTitle,
                    [.. state.ChartPoints.Select(static point => new ChartPointNode(point.Label, point.Value))],
                    SeriesName: "Current",
                    SourceNeuronUri: state.SourceNeuronUri,
                    From: state.ActiveSource,
                    Prefix: "€")
                : null,
            Table(
                $"{state.ChartTitle} ({state.Rows.Length} row(s))",
                state.Columns,
                state.Rows.Select(r => (IReadOnlyList<string>)r).ToArray(),
                sourceNeuronUri: state.SourceNeuronUri) with
            {
                From = state.ActiveSource,
            }
        ];

        return BindOwner(root, AppOwner);
    }

    public static View Render(DataStudioAppState state) => new("Data Studio", RenderNode(state));
}
