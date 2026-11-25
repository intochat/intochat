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

    // The chart is a `chart` data node drawn from the app's own state; no ui.chart neuron is mounted (step P-Data/Usage).
    [Theory]
    [InlineData("bar", "columns")]
    [InlineData("line", "line")]
    [InlineData("pie", "pie")]
    public void DataStudioDrawsItsChartFromState(string kind, string drawnKind)
    {
        var points = new[] { new InoChartPoint("p1", "Proposal", 271200), new InoChartPoint("p2", "Contract", 96000) };
        var result = new DataQueryResult("Pipeline", ["Stage", "Amount"], [["Proposal", "271200"], ["Contract", "96000"]], kind, points, "Summary", "neuron://local/postgres/main#query-result", 367200, 183600, 96000, 271200);
        var state = DataStudioDecisions.DecideQueryResult(new DataStudioAppState(), "/work/q4.csv", "SELECT Stage, Amount FROM /work/q4.csv", kind, result, ["/work/q4.csv"], "local");

        var root = DataStudioViewRenderer.Render(state).Root;

        var chart = Assert.Single(Find<Chart>(root));
        Assert.Equal(drawnKind, chart.Kind);
        Assert.Equal("Pipeline", chart.Title);
        Assert.Equal([new ChartPointNode("Proposal", 271200), new ChartPointNode("Contract", 96000)], chart.Points);
        Assert.Equal("€", chart.Prefix);
        Assert.Equal("/work/q4.csv", chart.From);
        Assert.Equal("neuron://local/postgres/main#query-result", chart.SourceNeuronUri);
        Assert.DoesNotContain(Find<NeuronRef>(root), static mount => mount.Kind.StartsWith("ui.", StringComparison.Ordinal));
    }

    [Fact]
    public void DataStudioWithoutAQueryDrawsNoChart()
    {
        var root = DataStudioViewRenderer.Render(new DataStudioAppState()).Root;

        Assert.Empty(Find<Chart>(root));
        Assert.Empty(Find<NeuronRef>(root));
    }

    private static IEnumerable<T> Find<T>(object? value) where T : Node => value switch
    {
        null or string => [],
        T match => [match, .. match.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).SelectMany(p => Find<T>(p.GetValue(match)))],
        System.Collections.IEnumerable items => items.Cast<object?>().SelectMany(Find<T>),
        Node or View => value.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).SelectMany(p => Find<T>(p.GetValue(value))),
        _ => [],
    };

    private static IEnumerable<Button> ExtractButtons(object? value) => value switch
    {
        null => [],
        Button btn => [btn],
        System.Collections.IEnumerable items => items.Cast<object?>().SelectMany(ExtractButtons),
        Node or View => value.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).SelectMany(p => ExtractButtons(p.GetValue(value))),
        _ => [],
    };
}
