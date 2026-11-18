/*
# Data Studio
Role: Default
Shipped: true
Operations: open, query, group-by, switch-chart, export-csv, to-markdraw

Interactive data analysis, SQL/CSV/JSON exploration, and chart visualization studio Neuron (apps/data).
Powered by DataAnalyticsEngine and IFilesVfsService (/work/*.csv, /work/*.tsv, /work/*.json, and relational tables),
and synchronized with Ino chat and Markdraw Studio (apps/markdraw).
*/
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.Markdraw;
using IntoChat.OS.Framework.Logging;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.UI;

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
    public DataStudioAppState() : this("", "", "", "bar", [], [], [], 0, 0, 0, 0, "No dataset loaded.", "neuron://local/apps/data", []) { }

    [Invariant("ChartKind is bar, line, or pie."), JsonIgnore]
    public bool ValidChartKind => ChartKind is "bar" or "line" or "pie";

    [Invariant("Dataset collections are never null."), JsonIgnore]
    public bool CollectionsNonNull => Columns is not null && Rows is not null && ChartPoints is not null && WorkspaceDatasets is not null;

    public Rule HasDatasetRows() => Decision.Rule("dataset_empty", Rows, static r => r.Length == 0, "Load a dataset before exporting.");

    public Rule SupportedChartKind(string? kind) => Decision.Rule(
        "invalid_chart_kind",
        kind,
        static k => string.IsNullOrWhiteSpace(k) || k.Trim().ToLowerInvariant() is not ("bar" or "line" or "pie"),
        "Chart kind must be bar, line, or pie.");

    public Decision<DataStudioAppState> SwitchChart([Sample("bar", "line", "pie", "radar")] string kind) =>
        Decision.Check("switch-chart", SupportedChartKind(kind)) ?? Decision.Accept(DataStudioDecisions.DecideSwitchChart(this, kind));

    public Decision<DataStudioAppState> RecordCsvExport([Sample("/work/analytics-export.csv")] string targetPath) =>
        Decision.Check("export-csv", HasDatasetRows(), Decision.Rule("empty_export_path", targetPath, static p => string.IsNullOrWhiteSpace(p), "Target CSV path is required."))
        ?? Decision.Accept(DataStudioDecisions.DecideExportCsv(this, targetPath.Trim(), WorkspaceDatasets));

    public Decision<DataStudioAppState> RecordMarkdrawLineage([Sample("/work/data-lineage.markdraw")] string docPath) =>
        Decision.Check("to-markdraw", HasDatasetRows(), Decision.Rule("empty_markdraw_path", docPath, static p => string.IsNullOrWhiteSpace(p), "Target Markdraw path is required."))
        ?? Decision.Accept(DataStudioDecisions.DecideToMarkdraw(this, docPath.Trim()));
}

[Neuron("app.data"), Screen("app.data", Title = "Data Studio")]
public sealed class DataStudioApp : AggregateScreen<DataStudioAppState>, IWithFilesVfs
{
    protected override Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        return base.OnActivate(cancellationToken);
    }

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

    public new DataStudioAppState State => base.State;
    public View Render() => PreviewView(State);

    private string CurrentBrainId
    {
        get
        {
            try { return Id.Brain.Value; }
            catch (InvalidOperationException error)
            {
                OsConsoleLog.For<DataStudioApp>().LogSwallowed(error, "Unattached neuron has no assigned brain id in isolated tests.");
                return string.Empty;
            }
        }
    }

    [On]
    public Task Open(DataStudioOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = string.IsNullOrWhiteSpace(request.Source) ? State.ActiveSource : DataStudioDecisions.NormalizeSource(request.Source);
        return RunAndSetQueryResultAsync(source, DataStudioDecisions.BuildDefaultQueryForSource(source), State.ChartKind);
    }

    [On]
    public Task Query(DataStudioQuery request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var queryText = string.IsNullOrWhiteSpace(request.Query) ? State.ActiveQuery : request.Query.Trim();
        var explicitFile = DataAnalyticsEngine.TryExtractDatasetFileToken(queryText);
        var source = !string.IsNullOrWhiteSpace(explicitFile)
            ? DataStudioDecisions.NormalizeSource(explicitFile)
            : queryText.Contains("customers", StringComparison.OrdinalIgnoreCase) ? "postgres:customers" : State.ActiveSource;
        return RunAndSetQueryResultAsync(source, queryText, State.ChartKind);
    }

    [On]
    public Task GroupBy(DataStudioGroupBy request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var col = string.IsNullOrWhiteSpace(request.Column) ? (State.Columns.Length > 2 ? State.Columns[2] : State.Columns.FirstOrDefault() ?? "Stage") : request.Column.Trim();
        var fn = string.IsNullOrWhiteSpace(request.Function) ? "SUM" : request.Function.Trim().ToUpperInvariant();
        var from = State.ActiveSource.StartsWith("postgres:", StringComparison.OrdinalIgnoreCase) ? State.ActiveSource["postgres:".Length..] : State.ActiveSource;
        return RunAndSetQueryResultAsync(State.ActiveSource, $"SELECT {col}, {fn}(Amount) FROM {from} GROUP BY {col}", State.ChartKind);
    }

    [On]
    public Task SwitchChart(DataStudioSwitchChart request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return string.IsNullOrWhiteSpace(request.Kind)
            ? Show(DataStudioDecisions.DecideSwitchChart(State, "bar"))
            : ApplyDecisionAsync(State.SwitchChart(DataStudioDecisions.NormalizeChartKind(request.Kind)));
    }

    [On]
    public async Task ExportCsv(DataStudioExportCsv request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stem = Path.GetFileNameWithoutExtension(State.ActiveSource.Replace("postgres:", string.Empty, StringComparison.OrdinalIgnoreCase));
        var targetPath = string.IsNullOrWhiteSpace(request.TargetPath) ? $"/work/{(string.IsNullOrWhiteSpace(stem) ? "analytics-export" : stem)}-export.csv" : DataStudioDecisions.NormalizeCsvExportPath(request.TargetPath);
        targetPath = await TryWriteVfsTextAsync(targetPath, DataAnalyticsEngine.SerializeToCsv(State.Columns, State.Rows));
        await Show(DataStudioDecisions.DecideExportCsv(State, targetPath, await ListWorkspaceDatasetsAsync(State.ActiveSource)));
    }

    [On]
    public async Task ToMarkdraw(DataStudioToMarkdraw request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var docPath = string.IsNullOrWhiteSpace(request.Document) ? "/work/data-lineage.markdraw" : MarkdrawApp.NormalizeDocumentPath(request.Document);
        var summary = $"{State.Rows.Length} rows · Total €{State.Total:N0} · Avg €{State.Average:N0}";
        docPath = await TryWriteVfsTextAsync(docPath, MarkdrawSceneEngine.BuildDataLineageMarkdraw(State.ChartTitle, State.Columns, summary, State.SourceNeuronUri));
        await Show(DataStudioDecisions.DecideToMarkdraw(State, docPath));
    }

    public Task GroupByColumnAsync(string column) => GroupBy(new DataStudioGroupBy(column, "SUM"));
    public Task ExportCsvDefaultAsync() => State.HasDatasetRows().Broken ? ApplyDecisionAsync(State.RecordCsvExport("/work/analytics-export.csv")) : ExportCsv(new DataStudioExportCsv());
    public Task DiagramInMarkdrawDefaultAsync() => State.HasDatasetRows().Broken ? ApplyDecisionAsync(State.RecordMarkdrawLineage("/work/data-lineage.markdraw")) : ToMarkdraw(new DataStudioToMarkdraw("/work/data-lineage.markdraw"));

    protected override Node Render(DataStudioAppState state) => DataStudioViewRenderer.RenderNode(state);

    private async Task<string> TryWriteVfsTextAsync(string path, string content)
    {
        if (FilesVfs is null) { return path; }
        try { return (await FilesVfs.WriteTextAsync(path, content)).VfsPath; }
        catch (Exception ex) { OsConsoleLog.For<DataStudioApp>().LogSwallowed(ex, "Failed to write text to VFS path."); return path; }
    }

    private async Task RunAndSetQueryResultAsync(string source, string queryText, string preferredChartKind)
    {
        var effectiveQuery = !queryText.Contains(source, StringComparison.OrdinalIgnoreCase)
            && !source.StartsWith("postgres:", StringComparison.OrdinalIgnoreCase)
            && DataAnalyticsEngine.TryExtractDatasetFileToken(queryText) is null
            ? $"{queryText} FROM {source}"
            : queryText;

        var result = await DataAnalyticsEngine.ExecuteAsync(effectiveQuery, database: source, filesVfs: FilesVfs, brain: Context.Caller?.Brain ?? default);
        await Show(DataStudioDecisions.DecideQueryResult(State, source, queryText, preferredChartKind, result, await ListWorkspaceDatasetsAsync(source), CurrentBrainId));
    }

    private async Task<string[]> ListWorkspaceDatasetsAsync(string activeSource)
    {
        var list = string.IsNullOrWhiteSpace(activeSource) ? new List<string>() : [activeSource];
        if (FilesVfs is not null)
        {
            try
            {
                list.AddRange((await FilesVfs.ListFilesAsync())
                    .Where(f => Path.GetExtension(f.Name).ToLowerInvariant() is ".csv" or ".tsv" or ".json")
                    .Select(f => f.VfsPath));
            }
            catch (Exception ex) { OsConsoleLog.For<DataStudioApp>().LogSwallowed(ex, "Failed to list VFS files for data sources."); }
        }
        return [.. list.Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
