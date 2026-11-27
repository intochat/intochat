/*
# Data Studio
Role: Default
Shipped: true
Operations: open, query, group-by, switch-chart, export-csv, to-markdraw

Interactive data analysis, SQL/CSV/JSON exploration, and chart visualization studio Neuron (apps/data).
Powered by DataAnalyticsEngine over workspace files (/work/*.csv, *.tsv, *.json, read and written through IWorkspaceFiles)
and relational tables, and synchronized with Ino chat and Markdraw Studio (apps/markdraw).
*/
using IntoChat.OS.Host.Apps;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.Markdraw;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.UI;
using IntoChat.OS.Host.Logging;

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

[Neuron("app.data"), Screen("app.data", Title = "Data Studio", Icon = "bar-chart-2", Category = "Studio")]
public sealed partial class DataStudioApp : AggregateScreen<DataStudioAppState>
{
    // The brain's workspace files: datasets are read from it, exports and diagrams written to it.
    [Slot(Key = WorkspaceFilesDefaults.Key, Required = false)] private partial IWorkspaceFiles? WorkspaceFiles { get; }

    protected override Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        return base.OnActivate(cancellationToken);
    }

    public const string AppName = "data";

    public static AppBundle Bundle => AppBundle.FromScreen<DataStudioApp>(
        "data",
        "1.0.0",
        description: "Interactive data exploration, SQL/CSV/JSON relational analytics, KPI metrics, and live chart visualization synced with Ino and Markdraw.",
        operations:
        [
            new("open", "Open a CSV, TSV or JSON dataset by its workspace path or a connected PostgreSQL table."),
            new("query", "Run a SQL or natural-language analytical query (WHERE, GROUP BY, SUM/AVG/COUNT/MIN/MAX, ORDER BY, LIMIT)."),
            new("group-by", "Aggregate the active dataset by a column using SUM, AVG, COUNT, MIN, or MAX."),
            new("switch-chart", "Switch the active chart visualization between bar, line, and pie."),
            new("export-csv", "Export the current filtered or aggregated table as a CSV file in the workspace (/work/*.csv)."),
            new("to-markdraw", "Generate a live Markdraw data-lineage diagram from the active dataset and chart, saved in the workspace."),
        ],
        studio: true,
        toolbar:
        [
            new("open", Icon: "database", Label: "Open Dataset"),
            new("export-csv", Icon: "download", Label: "Export CSV"),
            new("to-markdraw", Icon: "pen-tool", Label: "Diagram in Markdraw"),
        ]);

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
        // With no rows the export is refused by the same rule the button's guard shows.
        if (State.HasDatasetRows().Broken) { await ApplyDecisionAsync(State.RecordCsvExport("/work/analytics-export.csv")); return; }
        var stem = Path.GetFileNameWithoutExtension(State.ActiveSource.Replace("postgres:", string.Empty, StringComparison.OrdinalIgnoreCase));
        var targetPath = string.IsNullOrWhiteSpace(request.TargetPath) ? $"/work/{(string.IsNullOrWhiteSpace(stem) ? "analytics-export" : stem)}-export.csv" : DataStudioDecisions.NormalizeCsvExportPath(request.TargetPath);
        var failed = await WriteToWorkspaceAsync(targetPath, DataAnalyticsEngine.SerializeToCsv(State.Columns, State.Rows));
        var next = DataStudioDecisions.DecideExportCsv(State, targetPath, await ListWorkspaceDatasetsAsync(State.ActiveSource));
        await Show(failed is null ? next : next with { Summary = $"{next.Summary} It is kept in this screen only: {failed}" });
    }

    [On]
    public async Task ToMarkdraw(DataStudioToMarkdraw request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (State.HasDatasetRows().Broken) { await ApplyDecisionAsync(State.RecordMarkdrawLineage("/work/data-lineage.markdraw")); return; }
        var docPath = string.IsNullOrWhiteSpace(request.Document) ? "/work/data-lineage.markdraw" : MarkdrawApp.NormalizeDocumentPath(request.Document);
        var summary = $"{State.Rows.Length} rows · Total €{State.Total:N0} · Avg €{State.Average:N0}";
        var failed = await WriteToWorkspaceAsync(docPath, MarkdrawSceneEngine.BuildDataLineageMarkdraw(State.ChartTitle, State.Columns, summary, State.SourceNeuronUri));
        var next = DataStudioDecisions.DecideToMarkdraw(State, docPath);
        await Show(failed is null ? next : next with { Summary = $"{next.Summary} It is kept in this screen only: {failed}" });
    }


    protected override Node Render(DataStudioAppState state) => DataStudioViewRenderer.RenderNode(state);


    private async Task RunAndSetQueryResultAsync(string source, string queryText, string preferredChartKind)
    {
        var effectiveQuery = !queryText.Contains(source, StringComparison.OrdinalIgnoreCase)
            && !source.StartsWith("postgres:", StringComparison.OrdinalIgnoreCase)
            && DataAnalyticsEngine.TryExtractDatasetFileToken(queryText) is null
            ? $"{queryText} FROM {source}"
            : queryText;

        var brain = Context.Caller?.Brain ?? default;
        var result = await WorkspaceDataset.Read(WorkspaceFiles, effectiveQuery) is { } dataset
            ? DataAnalyticsEngine.ExecuteInMemory(effectiveQuery, source, dataset.Text, dataset.Path, brain)
            : await DataAnalyticsEngine.ExecuteAsync(effectiveQuery, database: source, brain: brain);
        await Show(DataStudioDecisions.DecideQueryResult(State, source, queryText, preferredChartKind, result, await ListWorkspaceDatasetsAsync(source), CurrentBrainId));
    }

    // Writes the text as a workspace file; null when it is there, else why it is not.
    private async Task<string?> WriteToWorkspaceAsync(string path, string text)
    {
        if (WorkspaceFiles is not { } files) { return "this screen has no workspace."; }
        try
        {
            _ = await files.Write(path, System.Text.Encoding.UTF8.GetBytes(text));
            return null;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<DataStudioApp>().LogSwallowed(error, "writing a file to the workspace");
            return error.Message;
        }
    }

    // The active source and the datasets at the top of the workspace.
    private async Task<string[]> ListWorkspaceDatasetsAsync(string activeSource)
    {
        var list = string.IsNullOrWhiteSpace(activeSource) ? new List<string>() : [activeSource];
        if (WorkspaceFiles is { } files)
        {
            try
            {
                list.AddRange((await files.List("")).Where(file => !file.IsFolder && Path.GetExtension(file.Name).ToLowerInvariant() is ".csv" or ".tsv" or ".json").Select(file => file.Path));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                OsConsoleLog.For<DataStudioApp>().LogSwallowed(error, "listing the workspace's datasets");
            }
        }
        return [.. list.Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
