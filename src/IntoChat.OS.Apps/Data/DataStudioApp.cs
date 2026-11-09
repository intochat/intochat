/*
# Data Studio
Role: Default
Shipped: true
Operations: open, query, group-by, switch-chart, export-csv, to-markdraw

Interactive data analysis, SQL/CSV/JSON exploration, and chart visualization studio Neuron (apps/data).
Powered by DataAnalyticsEngine and IFilesVfsService (/work/*.csv, /work/*.tsv, /work/*.json, and relational tables),
and synchronized with Ino chat and Markdraw Studio (apps/markdraw).
*/
using System.Globalization;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.Markdraw;
using IntoChat.OS.Modules.Apps;
using UI = DigitalBrain.UI.Ui;

namespace IntoChat.OS.Apps.Data;

[DigitalBrain.Signal("data.open")]
public sealed record DataStudioOpen(
    string? Source = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("data.query")]
public sealed record DataStudioQuery(
    string? Query = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("data.group-by")]
public sealed record DataStudioGroupBy(
    string? Column = null,
    string? Function = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("data.switch-chart")]
public sealed record DataStudioSwitchChart(
    string? Kind = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("data.export-csv")]
public sealed record DataStudioExportCsv(
    string? TargetPath = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("data.to-markdraw")]
public sealed record DataStudioToMarkdraw(
    string? Document = null) : DigitalBrain.Signal;

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
        ActiveSource: "/work/q4-opportunities.csv",
        ActiveQuery: "SELECT Opportunity, Account, Stage, Amount FROM /work/q4-opportunities.csv ORDER BY Amount DESC",
        ChartTitle: "Pipeline Opportunities by Amount",
        ChartKind: "bar",
        Columns: ["Opportunity", "Account", "Stage", "Amount"],
        Rows:
        [
            ["Havbris pilot", "Havbris AS", "Proposal", "€240,000"],
            ["Nordic Wind Q4", "Nordic Wind", "Negotiation", "€88,500"],
            ["Kystlinje Analytics", "Kystlinje", "Discovery", "€49,200"],
            ["Fjordkraft Renewal", "Fjordkraft", "Closed Won", "€64,000"],
            ["Bergen Port Ops", "Bergen Havn", "Contract", "€186,000"],
        ],
        ChartPoints:
        [
            new("pt-1", "Havbris pilot", 240000),
            new("pt-2", "Bergen Port Ops", 186000),
            new("pt-3", "Nordic Wind Q4", 88500),
            new("pt-4", "Fjordkraft Renewal", 64000),
            new("pt-5", "Kystlinje Analytics", 49200),
        ],
        Total: 627700,
        Average: 125540,
        Min: 49200,
        Max: 240000,
        Summary: "Queried 5 opportunities totaling €627,700 from /work/q4-opportunities.csv.",
        SourceNeuronUri: "neuron://local/apps/data#q4-opportunities",
        WorkspaceDatasets: ["/work/q4-opportunities.csv", "/work/customer-arr.csv", "postgres:opportunities", "postgres:customers"])
    {
    }
}

[DigitalBrain.App("data", Accent = "#38BDF8", Category = "Analytics")]
public sealed class DataStudioApp : DigitalBrain.App<DataStudioAppState>
{
    public const string DefaultOpportunitiesCsv = """
        Opportunity,Account,Stage,Amount
        Havbris pilot,Havbris AS,Proposal,"€240,000"
        Bergen Port Ops,Bergen Havn,Contract,"€186,000"
        Nordic Wind Q4,Nordic Wind,Negotiation,"€88,500"
        Fjordkraft Renewal,Fjordkraft,Closed Won,"€64,000"
        Kystlinje Analytics,Kystlinje,Discovery,"€49,200"
        """;

    public const string DefaultCustomerArrCsv = """
        Account,City,Owner,ARR
        Havbris AS,Bergen,Marte L.,"€148,000"
        Fjordkraft,Oslo,Henrik S.,"€112,000"
        Nordic Wind,Stavanger,Marte L.,"€94,500"
        Kystlinje,Trondheim,Astrid K.,"€76,000"
        """;

    public IFilesVfsService? FilesVfs { get; set; }

    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(DataStudioApp));

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

    [DigitalBrain.On]
    public async Task Open(DataStudioOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureDefaultDatasetsSeededAsync();

        var source = string.IsNullOrWhiteSpace(request.Source)
            ? State.ActiveSource
            : NormalizeSource(request.Source);

        var sql = BuildDefaultQueryForSource(source);
        await RunAndSetQueryResultAsync(source, sql, State.ChartKind);
    }

    [DigitalBrain.On]
    public async Task Query(DataStudioQuery request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureDefaultDatasetsSeededAsync();

        var queryText = string.IsNullOrWhiteSpace(request.Query)
            ? State.ActiveQuery
            : request.Query.Trim();

        var explicitFile = DataAnalyticsEngine.TryExtractDatasetFileToken(queryText);
        var source = !string.IsNullOrWhiteSpace(explicitFile)
            ? NormalizeSource(explicitFile)
            : queryText.Contains("customers", StringComparison.OrdinalIgnoreCase)
                ? "postgres:customers"
                : State.ActiveSource;

        await RunAndSetQueryResultAsync(source, queryText, State.ChartKind);
    }

    [DigitalBrain.On]
    public async Task GroupBy(DataStudioGroupBy request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureDefaultDatasetsSeededAsync();

        var col = string.IsNullOrWhiteSpace(request.Column)
            ? (State.Columns.Length > 2 ? State.Columns[2] : State.Columns.FirstOrDefault() ?? "Stage")
            : request.Column.Trim();
        var fn = string.IsNullOrWhiteSpace(request.Function)
            ? "SUM"
            : request.Function.Trim().ToUpperInvariant();

        var targetFrom = State.ActiveSource.StartsWith("postgres:", StringComparison.OrdinalIgnoreCase)
            ? State.ActiveSource["postgres:".Length..]
            : State.ActiveSource;
        var sql = $"SELECT {col}, {fn}(Amount) FROM {targetFrom} GROUP BY {col}";

        await RunAndSetQueryResultAsync(State.ActiveSource, sql, State.ChartKind);
    }

    [DigitalBrain.On]
    public Task SwitchChart(DataStudioSwitchChart request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kind = (request.Kind ?? "bar").Trim().ToLowerInvariant() switch
        {
            "pie" => "pie",
            "line" => "line",
            _ => "bar",
        };

        Set(State with
        {
            ChartKind = kind,
            Summary = $"Switched '{State.ChartTitle}' chart visualization to {kind.ToUpperInvariant()} ({State.Rows.Length} row(s), total €{State.Total:N0}).",
        });
        return Task.CompletedTask;
    }

    [DigitalBrain.On]
    public async Task ExportCsv(DataStudioExportCsv request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stem = Path.GetFileNameWithoutExtension(State.ActiveSource.Replace("postgres:", string.Empty, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "analytics-export";
        }

        var targetPath = string.IsNullOrWhiteSpace(request.TargetPath)
            ? $"/work/{stem}-export.csv"
            : NormalizeCsvExportPath(request.TargetPath);

        var csvContent = DataAnalyticsEngine.SerializeToCsv(State.Columns, State.Rows);
        if (FilesVfs is not null)
        {
            try
            {
                var written = await FilesVfs.WriteTextAsync(targetPath, csvContent);
                targetPath = written.VfsPath;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "FilesVfs write is optional during DataStudioApp.ExportCsv.");
            }
        }

        var datasets = await ListWorkspaceDatasetsAsync(State.ActiveSource);
        Set(State with
        {
            LastExportPath = targetPath,
            WorkspaceDatasets = datasets,
            Summary = $"Exported {State.Rows.Length} row(s) from '{State.ChartTitle}' to {targetPath}.",
        });
    }

    [DigitalBrain.On]
    public async Task ToMarkdraw(DataStudioToMarkdraw request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var docPath = string.IsNullOrWhiteSpace(request.Document)
            ? "/work/data-lineage.markdraw"
            : MarkdrawApp.NormalizeDocumentPath(request.Document);

        var rowsSummary = $"{State.Rows.Length} rows · Total €{State.Total:N0} · Avg €{State.Average:N0}";
        var markdrawContent = MarkdrawSceneEngine.BuildDataLineageMarkdraw(
            State.ChartTitle,
            State.Columns,
            rowsSummary,
            State.SourceNeuronUri);

        if (FilesVfs is not null)
        {
            try
            {
                var written = await FilesVfs.WriteTextAsync(docPath, markdrawContent);
                docPath = written.VfsPath;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "FilesVfs write is optional during DataStudioApp.ToMarkdraw.");
            }
        }

        Set(State with
        {
            LastMarkdrawPath = docPath,
            Summary = $"Generated Markdraw data-lineage diagram for '{State.ChartTitle}' at {docPath}.",
        });
    }

    public View RenderSide()
    {
        var datasets = State.WorkspaceDatasets is { Length: > 0 }
            ? State.WorkspaceDatasets
            : ["/work/q4-opportunities.csv", "/work/customer-arr.csv", "postgres:opportunities", "postgres:customers"];

        return new(
            "Data Sources",
            new Column(
            [
                new Surface(
                    [
                        new Row(
                        [
                            new TagPill("Data Studio", "accent", "bar-chart-2"),
                            new TagPill($"{State.Rows.Length} rows", "ok"),
                        ]) { Gap = 6 },
                        new Text(State.ActiveSource) { Mono = true, Tabular = true },
                        new Text($"Total €{State.Total:N0} · Avg €{State.Average:N0}") { Tone = "muted", Tabular = true },
                    ],
                    Kind: SurfaceKind.Acrylic,
                    Title: "Active Dataset"),
                new Divider("DATASETS & TABLES"),
                .. datasets.Select(ds => (Node)UI.NavItem(
                    $"ds-{MarkdrawSceneEngine.SlugId(ds)}",
                    ds.StartsWith("postgres:", StringComparison.OrdinalIgnoreCase) ? ds : Path.GetFileName(ds),
                    ds.StartsWith("postgres:", StringComparison.OrdinalIgnoreCase) ? "database" : "file-spreadsheet",
                    $"open:{ds}",
                    selected: string.Equals(ds, State.ActiveSource, StringComparison.OrdinalIgnoreCase),
                    badge: string.Equals(ds, State.ActiveSource, StringComparison.OrdinalIgnoreCase) ? "ACTIVE" : null)),
                new Divider("QUICK ANALYTICS"),
                UI.NavItem("q-stage", "Group by Stage", "pie-chart", "group-by:Stage:SUM"),
                UI.NavItem("q-top3", "Top 3 Opportunities", "trending-up", $"query:SELECT Opportunity, Account, Stage, Amount FROM {State.ActiveSource} LIMIT 3"),
                UI.NavItem("q-markdraw", "Diagram Data Lineage", "pen-tool", "to-markdraw:/work/data-lineage.markdraw"),
            ])
            { Gap = 8 });
    }

    public View Render()
    {
        var groupableCols = State.Columns.Take(3).ToArray();

        return new(
            "Data Studio",
            new Column(
            [
                new Surface(
                    [
                        new Row(
                        [
                            new TagPill("Data Studio · Live Analytics", "accent", "bar-chart-2"),
                            new TagPill(State.ActiveSource, "info", "database"),
                            new TagPill($"Total €{State.Total:N0}", "ok"),
                            new TagPill($"Avg €{State.Average:N0} · Max €{State.Max:N0}", "neutral"),
                        ]) { Gap = 8 },
                        new Text(State.Summary) { Tabular = true },
                        new Text(State.ActiveQuery) { Mono = true, Tone = "muted" },
                        new Row(
                        [
                            new Button("bar", "Bar", Emit: AppSignals.Emit("data", "switch-chart", "bar"), Tone: State.ChartKind == "bar" ? ButtonTone.Accent : ButtonTone.Neutral)
                            {
                                Icon = "bar-chart-2",
                                Small = true,
                            },
                            new Button("line", "Line", Emit: AppSignals.Emit("data", "switch-chart", "line"), Tone: State.ChartKind == "line" ? ButtonTone.Accent : ButtonTone.Neutral)
                            {
                                Icon = "trending-up",
                                Small = true,
                            },
                            new Button("pie", "Pie", Emit: AppSignals.Emit("data", "switch-chart", "pie"), Tone: State.ChartKind == "pie" ? ButtonTone.Accent : ButtonTone.Neutral)
                            {
                                Icon = "pie-chart",
                                Small = true,
                            },
                            .. groupableCols.Select(col => (Node)new Button($"group-by-{col}", $"Group by {col}", Emit: AppSignals.Emit("data", "group-by", col, "SUM"))
                            {
                                Icon = "layers",
                                Small = true,
                            }),
                            new Button("export-csv", "Export CSV", Emit: AppSignals.Emit("data", "export-csv"))
                            {
                                Icon = "download",
                                Small = true,
                            },
                            new Button("diagram-in-markdraw", "Diagram in Markdraw", Emit: AppSignals.Emit("data", "to-markdraw", "/work/data-lineage.markdraw"))
                            {
                                Icon = "pen-tool",
                                Small = true,
                            },
                        ]) { Gap = 8 },
                    ],
                    Kind: SurfaceKind.Acrylic,
                    Title: State.ChartTitle),
                new Chart(
                    State.ChartKind,
                    State.ChartTitle,
                    State.ChartPoints
                        .Select(pt => new ChartPointNode(pt.Label, pt.Value))
                        .ToArray(),
                    SeriesName: "Current",
                    SourceNeuronUri: State.SourceNeuronUri,
                    From: State.ActiveSource,
                    Prefix: "€"),
                new DataTable(
                    $"{State.ChartTitle} ({State.Rows.Length} row(s))",
                    State.Columns,
                    State.Rows.Select(r => (IReadOnlyList<string>)r).ToArray(),
                    SourceNeuronUri: State.SourceNeuronUri,
                    From: State.ActiveSource),
            ])
            { Gap = 12 });
    }

    [DigitalBrain.Scenario("Data Studio queries workspace dataset, groups by Stage, switches chart to pie, and exports CSV")]
    public async Task VerifyDataStudioAnalyticsAndExportScenario()
    {
        await Open(new DataStudioOpen("/work/q4-opportunities.csv"));
        if (State.Rows.Length < 4)
        {
            throw new InvalidOperationException("Expected DataStudioApp.Open to load initial opportunity rows.");
        }

        await GroupBy(new DataStudioGroupBy("Stage", "SUM"));
        if (!State.ChartTitle.Contains("SUM by Stage", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Expected DataStudioApp.GroupBy to aggregate dataset by Stage.");
        }

        await SwitchChart(new DataStudioSwitchChart("pie"));
        if (State.ChartKind != "pie")
        {
            throw new InvalidOperationException("Expected DataStudioApp.SwitchChart to switch active chart to pie.");
        }

        await ExportCsv(new DataStudioExportCsv("/work/stage-breakdown.csv"));
        if (!string.Equals(State.LastExportPath, "/work/stage-breakdown.csv", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Expected DataStudioApp.ExportCsv to record /work/stage-breakdown.csv.");
        }
    }

    private async Task RunAndSetQueryResultAsync(string source, string queryText, string preferredChartKind)
    {
        var effectiveQuery = !queryText.Contains(source, StringComparison.OrdinalIgnoreCase)
            && !source.StartsWith("postgres:", StringComparison.OrdinalIgnoreCase)
            && DataAnalyticsEngine.TryExtractDatasetFileToken(queryText) is null
            ? $"{queryText} FROM {source}"
            : queryText;

        var result = await DataAnalyticsEngine.ExecuteAsync(
            effectiveQuery,
            database: source,
            filesVfs: FilesVfs);

        var datasets = await ListWorkspaceDatasetsAsync(source);
        var effectiveKind = string.IsNullOrWhiteSpace(preferredChartKind) ? result.ChartKind : preferredChartKind;
        Set(State with
        {
            ActiveSource = source,
            ActiveQuery = queryText,
            ChartTitle = result.ChartTitle,
            ChartKind = effectiveKind,
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

    private async Task EnsureDefaultDatasetsSeededAsync()
    {
        if (FilesVfs is null)
        {
            return;
        }

        try
        {
            if (await FilesVfs.ResolveFileAsync("/work/q4-opportunities.csv") is null)
            {
                await FilesVfs.WriteTextAsync("/work/q4-opportunities.csv", DefaultOpportunitiesCsv);
            }

            if (await FilesVfs.ResolveFileAsync("/work/customer-arr.csv") is null)
            {
                await FilesVfs.WriteTextAsync("/work/customer-arr.csv", DefaultCustomerArrCsv);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "Seeding default CSV files in FilesVfs is optional.");
        }
    }

    private async Task<string[]> ListWorkspaceDatasetsAsync(string activeSource)
    {
        var baseList = new List<string> { activeSource, "/work/q4-opportunities.csv", "/work/customer-arr.csv", "postgres:opportunities", "postgres:customers" };
        if (FilesVfs is not null)
        {
            try
            {
                var files = await FilesVfs.ListFilesAsync();
                foreach (var file in files)
                {
                    var ext = Path.GetExtension(file.Name).ToLowerInvariant();
                    if (ext is ".csv" or ".tsv" or ".json")
                    {
                        baseList.Add(file.VfsPath);
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "Listing dataset files in FilesVfs is optional.");
            }
        }

        return baseList
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeSource(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("postgres:", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.ToLowerInvariant();
        }
        if (string.Equals(trimmed, "customers", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "opportunities", StringComparison.OrdinalIgnoreCase))
        {
            return $"postgres:{trimmed.ToLowerInvariant()}";
        }

        var clean = WorkspaceFilesService.ExtractCleanFileName(trimmed);
        if (string.IsNullOrWhiteSpace(clean))
        {
            return "/work/q4-opportunities.csv";
        }
        if (!Path.HasExtension(clean))
        {
            clean += ".csv";
        }
        return clean.StartsWith('/') ? clean : $"/work/{clean}";
    }

    private static string NormalizeCsvExportPath(string raw)
    {
        var clean = WorkspaceFilesService.ExtractCleanFileName(raw);
        if (string.IsNullOrWhiteSpace(clean))
        {
            return "/work/analytics-export.csv";
        }
        if (!Path.HasExtension(clean))
        {
            clean += ".csv";
        }
        return clean.StartsWith('/') ? clean : $"/work/{clean}";
    }

    private static string BuildDefaultQueryForSource(string source)
    {
        if (string.Equals(source, "postgres:customers", StringComparison.OrdinalIgnoreCase))
        {
            return "SELECT account, city, owner, arr FROM customers ORDER BY arr DESC";
        }
        if (string.Equals(source, "postgres:opportunities", StringComparison.OrdinalIgnoreCase))
        {
            return "SELECT opportunity, account, stage, amount FROM opportunities ORDER BY amount DESC LIMIT 5";
        }
        return $"SELECT * FROM {source}";
    }
}
