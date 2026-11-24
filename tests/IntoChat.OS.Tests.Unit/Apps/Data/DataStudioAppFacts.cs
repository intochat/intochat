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

    private static IEnumerable<Button> ExtractButtons(object? value) => value switch
    {
        null => [],
        Button btn => [btn],
        System.Collections.IEnumerable items => items.Cast<object?>().SelectMany(ExtractButtons),
        Node or View => value.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).SelectMany(p => ExtractButtons(p.GetValue(value))),
        _ => [],
    };
}
