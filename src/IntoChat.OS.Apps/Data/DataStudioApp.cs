/*
# Data Studio
Role: Default
Shipped: true
Operations: open, query, group-by, switch-chart, export-csv, to-markdraw

Interactive data analysis, SQL/CSV/JSON exploration, and chart visualization studio Neuron (apps/data).
Powered by DataAnalyticsEngine and IFilesVfsService (/work/*.csv, /work/*.tsv, /work/*.json, and relational tables),
and synchronized with Ino chat and Markdraw Studio (apps/markdraw).
*/
using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.Markdraw;
using IntoChat.OS.Modules.Apps;
using UI = DigitalBrain.UI.Ui;

namespace IntoChat.OS.Apps.Data;

[Signal("data.open")]
public sealed record DataStudioOpen(string? Source = null) : Signal;

[Signal("data.query")]
public sealed record DataStudioQuery(string? Query = null) : Signal;

[Signal("data.group-by")]
public sealed record DataStudioGroupBy(string? Column = null, string? Function = null) : Signal;

[Signal("data.switch-chart")]
public sealed record DataStudioSwitchChart(string? Kind = null) : Signal;

[Signal("data.export-csv")]
public sealed record DataStudioExportCsv(string? TargetPath = null) : Signal;

[Signal("data.to-markdraw")]
public sealed record DataStudioToMarkdraw(string? Document = null) : Signal;

public sealed record DataStudioAppState(
    string ActiveSource,
    string ActiveQuery,
    string ChartTitle,
    string ChartKind,
    string[] Columns,
    string[][] Rows,
    InoChartPoint[] ChartPoints,
    double Total,
    double Average,
    double Min,
    double Max,
    string Summary,
    string SourceNeuronUri,
    string[] WorkspaceDatasets,
    string? LastExportPath = null,
    string? LastMarkdrawPath = null)
{
    public DataStudioAppState() : this(
        ActiveSource: "",
        ActiveQuery: "",
        ChartTitle: "",
        ChartKind: "bar",
        Columns: [],
        Rows: [],
        ChartPoints: [],
        Total: 0,
        Average: 0,
        Min: 0,
        Max: 0,
        Summary: "No dataset loaded.",
        SourceNeuronUri: "neuron://local/apps/data",
        WorkspaceDatasets: [])
    {
    }
}

[Neuron("app.data")]
public sealed class DataStudioApp : Screen<DataStudioAppState>, IWithFilesVfs
{
    public IFilesVfsService? FilesVfs { get; set; }

    public const string AppName = "data";

    public static AppBundle Bundle { get; } = new(
        "data",
        "1.0.0",
        new AppManifest(
            Title: "Data Studio",
            Description: "Interactive data exploration, SQL/CSV/JSON relational analytics, KPI metrics, and live chart visualization synced with Ino and Markdraw.",
            Operations:
            [
                new("open", "Open a CSV, TSV, JSON dataset from Files VFS or a connected PostgreSQL table."),
                new("query", "Run a SQL or natural-language analytical query (WHERE, GROUP BY, SUM/AVG/COUNT/MIN/MAX, ORDER BY, LIMIT)."),
                new("group-by", "Aggregate the active dataset by a column using SUM, AVG, COUNT, MIN, or MAX."),
                new("switch-chart", "Switch the active chart visualization between bar, line, and pie."),
                new("export-csv", "Export the current filtered or aggregated table to a CSV file in Files VFS (/work/*.csv)."),
                new("to-markdraw", "Generate a live Markdraw data-lineage diagram from the active dataset and chart."),
            ],
            Settings: [],
            Slots: [],
            Icon: "icon.svg",
            Dock: 3, Studio: true,
            Toolbar:
            [
                new("open", Icon: "database", Label: "Open Dataset"),
                new("export-csv", Icon: "download", Label: "Export CSV"),
                new("to-markdraw", Icon: "pen-tool", Label: "Diagram in Markdraw"),
            ]),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(DataStudioApp).AssemblyQualifiedName,
    };

    [On]
    public async Task Open(DataStudioOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = string.IsNullOrWhiteSpace(request.Source) ? State.ActiveSource : NormalizeSource(request.Source);
        await RunAndSetQueryResultAsync(source, BuildDefaultQueryForSource(source), State.ChartKind);
    }

    [On]
    public async Task Query(DataStudioQuery request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var queryText = string.IsNullOrWhiteSpace(request.Query) ? State.ActiveQuery : request.Query.Trim();
        var explicitFile = DataAnalyticsEngine.TryExtractDatasetFileToken(queryText);
        var source = !string.IsNullOrWhiteSpace(explicitFile)
            ? NormalizeSource(explicitFile)
            : queryText.Contains("customers", StringComparison.OrdinalIgnoreCase)
                ? "postgres:customers"
                : State.ActiveSource;
        await RunAndSetQueryResultAsync(source, queryText, State.ChartKind);
    }

    [On]
    public async Task GroupBy(DataStudioGroupBy request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var col = string.IsNullOrWhiteSpace(request.Column)
            ? (State.Columns.Length > 2 ? State.Columns[2] : State.Columns.FirstOrDefault() ?? "Stage")
            : request.Column.Trim();
        var fn = string.IsNullOrWhiteSpace(request.Function) ? "SUM" : request.Function.Trim().ToUpperInvariant();
        var targetFrom = State.ActiveSource.StartsWith("postgres:", StringComparison.OrdinalIgnoreCase)
            ? State.ActiveSource["postgres:".Length..]
            : State.ActiveSource;
        await RunAndSetQueryResultAsync(State.ActiveSource, $"SELECT {col}, {fn}(Amount) FROM {targetFrom} GROUP BY {col}", State.ChartKind);
    }

    [On]
    public Task SwitchChart(DataStudioSwitchChart request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kind = (request.Kind ?? "bar").Trim().ToLowerInvariant() switch
        {
            "pie" => "pie",
            "line" => "line",
            _ => "bar",
        };
        return Show(State with
        {
            ChartKind = kind,
            Summary = $"Switched '{State.ChartTitle}' chart visualization to {kind.ToUpperInvariant()} ({State.Rows.Length} row(s), total €{State.Total:N0}).",
        });
    }

    [On]
    public async Task ExportCsv(DataStudioExportCsv request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stem = Path.GetFileNameWithoutExtension(State.ActiveSource.Replace("postgres:", string.Empty, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(stem)) { stem = "analytics-export"; }

        var targetPath = string.IsNullOrWhiteSpace(request.TargetPath) ? $"/work/{stem}-export.csv" : NormalizeCsvExportPath(request.TargetPath);
        targetPath = await TryWriteVfsTextAsync(targetPath, DataAnalyticsEngine.SerializeToCsv(State.Columns, State.Rows), "DataStudioApp.ExportCsv");

        var datasets = await ListWorkspaceDatasetsAsync(State.ActiveSource);
        await Show(State with
        {
            LastExportPath = targetPath,
            WorkspaceDatasets = datasets,
            Summary = $"Exported {State.Rows.Length} row(s) from '{State.ChartTitle}' to {targetPath}.",
        });
    }

    [On]
    public async Task ToMarkdraw(DataStudioToMarkdraw request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var docPath = string.IsNullOrWhiteSpace(request.Document) ? "/work/data-lineage.markdraw" : MarkdrawApp.NormalizeDocumentPath(request.Document);
        var rowsSummary = $"{State.Rows.Length} rows · Total €{State.Total:N0} · Avg €{State.Average:N0}";
        var markdrawContent = MarkdrawSceneEngine.BuildDataLineageMarkdraw(State.ChartTitle, State.Columns, rowsSummary, State.SourceNeuronUri);
        docPath = await TryWriteVfsTextAsync(docPath, markdrawContent, "DataStudioApp.ToMarkdraw");

        await Show(State with
        {
            LastMarkdrawPath = docPath,
            Summary = $"Generated Markdraw data-lineage diagram for '{State.ChartTitle}' at {docPath}.",
        });
    }

    private async Task<string> TryWriteVfsTextAsync(string path, string content, string context)
    {
        _ = context;
        if (FilesVfs is null) { return path; }
        try { return (await FilesVfs.WriteTextAsync(path, content)).VfsPath; }
        catch (Exception ex)
        {
            _ = ex.Message;
            return path;
        }
    }

    public new DataStudioAppState State => base.State;
    public View Render() => Render(State);

    private static new Emit Emits<TSignal>(TSignal signal) where TSignal : Signal
    {
        ArgumentNullException.ThrowIfNull(signal);
        var alias = typeof(TSignal).GetCustomAttribute<SignalAttribute>(false)?.Alias ?? typeof(TSignal).Name;
        return new Emit("app.datastudio", alias, JsonSerializer.SerializeToElement(signal));
    }
    protected override View Render(DataStudioAppState state) => new(
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

    private async Task RunAndSetQueryResultAsync(string source, string queryText, string preferredChartKind)
    {
        var effectiveQuery = !queryText.Contains(source, StringComparison.OrdinalIgnoreCase)
            && !source.StartsWith("postgres:", StringComparison.OrdinalIgnoreCase)
            && DataAnalyticsEngine.TryExtractDatasetFileToken(queryText) is null
            ? $"{queryText} FROM {source}"
            : queryText;

        var result = await DataAnalyticsEngine.ExecuteAsync(effectiveQuery, database: source, filesVfs: FilesVfs);
        var datasets = await ListWorkspaceDatasetsAsync(source);
        await Show(State with
        {
            ActiveSource = source,
            ActiveQuery = queryText,
            ChartTitle = result.ChartTitle,
            ChartKind = string.IsNullOrWhiteSpace(preferredChartKind) ? result.ChartKind : preferredChartKind,
            Columns = result.Columns,
            Rows = result.Rows,
            ChartPoints = result.ChartPoints,
            Total = Math.Round(result.TotalValue, 2),
            Average = Math.Round(result.AverageValue, 2),
            Min = Math.Round(result.MinValue, 2),
            Max = Math.Round(result.MaxValue, 2),
            Summary = result.Summary,
            SourceNeuronUri = result.SourceUri,
            WorkspaceDatasets = datasets,
        });
    }

    private async Task<string[]> ListWorkspaceDatasetsAsync(string activeSource)
    {
        var baseList = new List<string>();
        if (!string.IsNullOrWhiteSpace(activeSource)) { baseList.Add(activeSource); }

        if (FilesVfs is not null)
        {
            try
            {
                foreach (var file in await FilesVfs.ListFilesAsync())
                {
                    if (Path.GetExtension(file.Name).ToLowerInvariant() is ".csv" or ".tsv" or ".json")
                    {
                        baseList.Add(file.VfsPath);
                    }
                }
            }
            catch (Exception ex)
            {
                _ = ex.Message;
            }
        }

        return baseList.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string NormalizeSource(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("postgres:", StringComparison.OrdinalIgnoreCase)) { return trimmed.ToLowerInvariant(); }
        if (string.Equals(trimmed, "customers", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "opportunities", StringComparison.OrdinalIgnoreCase))
        {
            return $"postgres:{trimmed.ToLowerInvariant()}";
        }
        return NormalizeCsvPath(trimmed, "/work/q4-opportunities.csv");
    }

    private static string NormalizeCsvExportPath(string raw) => NormalizeCsvPath(raw, "/work/analytics-export.csv");

    private static string NormalizeCsvPath(string raw, string fallback)
    {
        var clean = WorkspaceFilesService.ExtractCleanFileName(raw);
        if (string.IsNullOrWhiteSpace(clean)) { return fallback; }
        if (!Path.HasExtension(clean)) { clean += ".csv"; }
        return clean.StartsWith('/') ? clean : $"/work/{clean}";
    }

    private static string BuildDefaultQueryForSource(string source) => source.ToLowerInvariant() switch
    {
        "postgres:customers" => "SELECT account, city, owner, arr FROM customers ORDER BY arr DESC",
        "postgres:opportunities" => "SELECT opportunity, account, stage, amount FROM opportunities ORDER BY amount DESC LIMIT 5",
        _ => $"SELECT * FROM {source}",
    };
}
