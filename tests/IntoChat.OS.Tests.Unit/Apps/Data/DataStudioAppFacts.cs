using DigitalBrain;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Data;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.Markdraw;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Data;

public sealed class DataStudioAppFacts
{
    public static IEnumerable<TheoryDataRow<ScreenOperationCase>> DataStudioOperations
        => ScreenBaseline.Cases(
            typeof(DataStudioApp),
            os => os.RegisterCatalog());

    [Theory, MemberData(nameof(DataStudioOperations))]
    public Task DataStudioBaseline(ScreenOperationCase operation) => ScreenBaseline.RunAsync(operation);

    [Fact]
    public async Task DataStudioBddScenarioPasses()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("test", "test", new BrainId("test")));
        await using var os = TestOS.Local();
        os.RegisterCatalog();
        os.Register(typeof(DataStudioApp).Assembly);
        var screen = os.Get<IScreen>(new NeuronId(new BrainId("test"), "app.data", "default"));
        Assert.NotNull(await screen.Read());
    }

    [Fact]
    public async Task DataStudioQueriesCsvGroupsByColumnSwitchesChartExportsAndDiagramsInMarkdraw()
    {
        var ct = TestContext.Current.CancellationToken;
        using var vfsRoot = new TempDirectory("intochat-vfs-datastudio-");
        var vfs = new WorkspaceFilesService(rootDirectory: vfsRoot.Path);
        await using var os = TestOS.Local();
        os.RegisterCatalog();

        var dataStudio = await os.Install<DataStudioApp>();
        dataStudio.App.FilesVfs = vfs;
        await vfs.WriteTextAsync("/work/q4-opportunities.csv", "Opportunity,Account,Stage,Amount\nHavbris pilot,Havbris AS,Proposal,\"€240,000\"\nBergen Port Ops,Bergen Havn,Contract,\"€186,000\"\nNordic Wind Q4,Nordic Wind,Negotiation,\"€88,500\"\nFjordkraft Renewal,Fjordkraft,Closed Won,\"€64,000\"\nKystlinje Analytics,Kystlinje,Discovery,\"€49,200\"\n", ct);

        await dataStudio.Request("open", new DataStudioOpen("/work/q4-opportunities.csv"));
        Assert.Equal("/work/q4-opportunities.csv", dataStudio.App.State.ActiveSource);
        Assert.Equal(5, dataStudio.App.State.Rows.Length);
        Assert.Equal(627700, dataStudio.App.State.Total);

        // Filter via WHERE predicate
        await dataStudio.Request("query", new DataStudioQuery("SELECT Opportunity, Account, Stage, Amount FROM /work/q4-opportunities.csv WHERE Amount > 80000"));
        Assert.Equal(3, dataStudio.App.State.Rows.Length);
        Assert.Equal(514500, dataStudio.App.State.Total);

        // Group by Stage
        await dataStudio.Request("group-by", new DataStudioGroupBy("Stage", "SUM"));
        Assert.Contains("SUM by Stage", dataStudio.App.State.ChartTitle, StringComparison.OrdinalIgnoreCase);

        // Switch chart kind to pie
        await dataStudio.Request("switch-chart", new DataStudioSwitchChart("pie"));
        Assert.Equal("pie", dataStudio.App.State.ChartKind);

        // Export CSV to Files VFS
        await dataStudio.Request("export-csv", new DataStudioExportCsv("/work/stage-summary.csv"));
        Assert.Equal("/work/stage-summary.csv", dataStudio.App.State.LastExportPath);
        var exportedCsv = await vfs.ReadTextAsync("/work/stage-summary.csv", ct);
        Assert.NotNull(exportedCsv);
        Assert.Contains("Stage,Count,SUM(Amount)", exportedCsv, StringComparison.Ordinal);

        // Generate Markdraw data-lineage diagram in Files VFS
        await dataStudio.Request("to-markdraw", new DataStudioToMarkdraw("/work/stage-lineage.markdraw"));
        Assert.Equal("/work/stage-lineage.markdraw", dataStudio.App.State.LastMarkdrawPath);
        var lineageText = await vfs.ReadTextAsync("/work/stage-lineage.markdraw", ct);
        Assert.NotNull(lineageText);
        Assert.Contains("```markdraw", lineageText, StringComparison.Ordinal);
        Assert.Contains("SUM by Stage", lineageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InoDataStudioAndMarkdrawShareLiveDatasetsAndLineageInVfs()
    {
        var ct = TestContext.Current.CancellationToken;
        using var vfsRoot = new TempDirectory("intochat-vfs-datastudio-");
        var vfs = new WorkspaceFilesService(rootDirectory: vfsRoot.Path);
        await using var os = TestOS.Local();
        os.RegisterCatalog();

        var dataStudio = await os.Install<DataStudioApp>();
        dataStudio.App.FilesVfs = vfs;
        await vfs.WriteTextAsync("/work/customer-arr.csv", "Account,City,Owner,ARR\nHavbris AS,Bergen,Marte L.,\"€148,000\"\nFjordkraft,Oslo,Henrik S.,\"€112,000\"\nNordic Wind,Stavanger,Marte L.,\"€94,500\"\nKystlinje,Trondheim,Astrid K.,\"€76,000\"\n", ct);
        await dataStudio.Request("open", new DataStudioOpen("/work/customer-arr.csv"));
        Assert.Equal(4, dataStudio.App.State.Rows.Length);
        Assert.Equal(430500, dataStudio.App.State.Total);

        var ino = await os.Install<InoApp>();
        ino.App.FilesVfs = vfs;
        await ino.Request("query-db", new InoQueryDb("SELECT Account, Owner, ARR FROM /work/customer-arr.csv GROUP BY Owner"));
        var chartCard = Assert.Single(ino.App.State.ActiveCards, c => c.Kind == "chart");
        Assert.NotNull(chartCard.ChartPoints);
        Assert.Equal("Marte L.", chartCard.ChartPoints![0].Label);
        Assert.Equal(242500, chartCard.ChartPoints[0].Value);
    }

    [Fact]
    public void DataStudioButtonsCommandTheirOwnView()
    {
        var view = IntoChat.OS.UI.ScreenPreview.With<DataStudioApp, View>(app => app.Render());
        Assert.NotNull(view);

        var buttons = ExtractButtons(view).ToArray();
        Assert.NotEmpty(buttons);
        foreach (var button in buttons.Where(b => b.Command != null))
        {
            Assert.Equal(".", button.Command!.Target);
        }
    }

    [Fact]
    public void DataStudioCleanStateDecideRenderBehavior()
    {
        var initial = new DataStudioAppState();
        Assert.Equal("", initial.ActiveSource);
        Assert.Equal("bar", initial.ChartKind);

        var switched = DataStudioDecisions.DecideSwitchChart(initial, "line");
        Assert.Equal("line", switched.ChartKind);

        var points = new[] { new InoChartPoint("p1", "Stage 1", 1000) };
        var queryResult = new DataQueryResult("Chart Title", ["Stage", "Amount"], [["Stage 1", "1000"]], "line", points, "Summary", "neuron://local/data", 1000, 1000, 1000, 1000);
        var decided = DataStudioDecisions.DecideQueryResult(switched, "/work/test.csv", "SELECT Stage, Amount FROM /work/test.csv", "line", queryResult, ["/work/test.csv"], "local");

        Assert.Equal("/work/test.csv", decided.ActiveSource);
        Assert.Equal("Chart Title", decided.ChartTitle);
        Assert.Equal(1000, decided.Total);
        Assert.Single(decided.Rows);

        var rendered = DataStudioViewRenderer.Render(decided);
        Assert.Equal("Data Studio", rendered.Title);
    }

    private static IEnumerable<Button> ExtractButtons(object? value) => value switch
    {
        null => [],
        Button btn => [btn],
        System.Collections.IEnumerable items => items.Cast<object?>().SelectMany(ExtractButtons),
        Node or View => value.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).SelectMany(p => ExtractButtons(p.GetValue(value))),
        _ => [],
    };
}
