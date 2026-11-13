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
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.Markdraw;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.Logging.Abstractions;

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

    private string CurrentBrainId
    {
        get
        {
            try { return Id.Brain.Value is { Length: > 0 } b ? b : "local"; }
            catch (InvalidOperationException error)
            {
                NullLogger.Instance.LogSwallowed(error, "Unattached neuron uses default local brain id in isolated tests.");
                return "local";
            }
        }
    }

    [On]
    public async Task Open(DataStudioOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = string.IsNullOrWhiteSpace(request.Source) ? State.ActiveSource : DataStudioDecisions.NormalizeSource(request.Source);
        await RunAndSetQueryResultAsync(source, DataStudioDecisions.BuildDefaultQueryForSource(source), State.ChartKind);
    }

    [On]
    public async Task Query(DataStudioQuery request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var queryText = string.IsNullOrWhiteSpace(request.Query) ? State.ActiveQuery : request.Query.Trim();
        var explicitFile = DataAnalyticsEngine.TryExtractDatasetFileToken(queryText);
        var source = !string.IsNullOrWhiteSpace(explicitFile)
            ? DataStudioDecisions.NormalizeSource(explicitFile)
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
        return Show(DataStudioDecisions.DecideSwitchChart(State, request.Kind));
    }

    [On]
    public async Task ExportCsv(DataStudioExportCsv request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stem = Path.GetFileNameWithoutExtension(State.ActiveSource.Replace("postgres:", string.Empty, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(stem)) { stem = "analytics-export"; }

        var targetPath = string.IsNullOrWhiteSpace(request.TargetPath) ? $"/work/{stem}-export.csv" : DataStudioDecisions.NormalizeCsvExportPath(request.TargetPath);
        targetPath = await TryWriteVfsTextAsync(targetPath, DataAnalyticsEngine.SerializeToCsv(State.Columns, State.Rows), "DataStudioApp.ExportCsv");

        var datasets = await ListWorkspaceDatasetsAsync(State.ActiveSource);
        await Show(DataStudioDecisions.DecideExportCsv(State, targetPath, datasets));
    }

    [On]
    public async Task ToMarkdraw(DataStudioToMarkdraw request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var docPath = string.IsNullOrWhiteSpace(request.Document) ? "/work/data-lineage.markdraw" : MarkdrawApp.NormalizeDocumentPath(request.Document);
        var rowsSummary = $"{State.Rows.Length} rows · Total €{State.Total:N0} · Avg €{State.Average:N0}";
        var markdrawContent = MarkdrawSceneEngine.BuildDataLineageMarkdraw(State.ChartTitle, State.Columns, rowsSummary, State.SourceNeuronUri);
        docPath = await TryWriteVfsTextAsync(docPath, markdrawContent, "DataStudioApp.ToMarkdraw");

        await Show(DataStudioDecisions.DecideToMarkdraw(State, docPath));
    }

    private async Task<string> TryWriteVfsTextAsync(string path, string content, string context)
    {
        _ = context;
        if (FilesVfs is null) { return path; }
        try { return (await FilesVfs.WriteTextAsync(path, content)).VfsPath; }
        catch (Exception ex)
        {
            NullLogger.Instance.LogSwallowed(ex, "Failed to write text to VFS path.");
            return path;
        }
    }

    public new DataStudioAppState State => base.State;
    public View Render() => Render(State);

    protected override View Render(DataStudioAppState state) => DataStudioViewRenderer.Render(state);

    private async Task RunAndSetQueryResultAsync(string source, string queryText, string preferredChartKind)
    {
        var effectiveQuery = !queryText.Contains(source, StringComparison.OrdinalIgnoreCase)
            && !source.StartsWith("postgres:", StringComparison.OrdinalIgnoreCase)
            && DataAnalyticsEngine.TryExtractDatasetFileToken(queryText) is null
            ? $"{queryText} FROM {source}"
            : queryText;

        var result = await DataAnalyticsEngine.ExecuteAsync(effectiveQuery, database: source, filesVfs: FilesVfs);
        var datasets = await ListWorkspaceDatasetsAsync(source);
        await Show(DataStudioDecisions.DecideQueryResult(State, source, queryText, preferredChartKind, result, datasets, CurrentBrainId));
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
                NullLogger.Instance.LogSwallowed(ex, "Failed to list VFS files for data sources.");
            }
        }

        return baseList.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
