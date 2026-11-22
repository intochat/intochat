using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Controls;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

public sealed record ChartParentState;

/*
# Chart parent

A test parent that slots two live charts: one of a known kind and one whose kind no chart can draw.
*/
[Neuron("test.chart-parent")]
public sealed class ChartParent : Screen<ChartParentState>
{
    protected override View Render(ChartParentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new("Parent", new Column([
            Child<LiveChartNeuron, LiveChartProps>("spend", new LiveChartProps("Spend", "line")),
            Child<LiveChartNeuron, LiveChartProps>("odd", new LiveChartProps("Odd", "donut")),
        ]));
    }
}

public sealed class LiveChartNeuronFacts
{
    private static LivePoint Point(string id, string label = "x", double value = 1) => new(id, label, value);

    [Fact]
    public async Task Replace_sets_points_and_bumps_version()
    {
        await using var chart = new ControlHarness(typeof(LiveChartNeuron), key: "spend");

        await chart.Send(new Replace([Point("a", "Mon", 3), Point("b", "Tue", 4)]));

        Assert.Equal([new ChartChanged(1)], chart.Published());
        Assert.Equal(new LiveChartView("spend", 1, [new ChartPointNode("Mon", 3), new ChartPointNode("Tue", 4)]), await chart.Drawn<LiveChartView>());
    }

    [Fact]
    public async Task Append_same_EventId_is_ignored()
    {
        await using var chart = new ControlHarness(typeof(LiveChartNeuron));

        await chart.Send(new Append(Point("a")));
        await chart.Send(new Append(Point("a", "other", 9)));

        Assert.Equal([new ChartChanged(1)], chart.Published());
        Assert.Single((await chart.Drawn<LiveChartView>()).Points);
    }

    [Fact]
    public async Task Append_past_cap_keeps_last_200()
    {
        await using var chart = new ControlHarness(typeof(LiveChartNeuron), state: new LiveChartState { Points = [.. Enumerable.Range(0, 200).Select(i => Point($"e{i}", $"p{i}"))] });

        await chart.Send(new Append(Point("e200", "p200")));

        var points = (await chart.Drawn<LiveChartView>()).Points;
        Assert.Equal(200, points.Count);
        Assert.Equal("p1", points[0].Label);
        Assert.Equal("p200", points[^1].Label);
    }

    [Fact]
    public async Task Replace_past_cap_keeps_last_200()
    {
        await using var chart = new ControlHarness(typeof(LiveChartNeuron));

        await chart.Send(new Replace([.. Enumerable.Range(0, 250).Select(i => Point($"e{i}", $"p{i}"))]));

        var points = (await chart.Drawn<LiveChartView>()).Points;
        Assert.Equal(200, points.Count);
        Assert.Equal("p50", points[0].Label);
    }

    [Fact]
    public async Task Trimmed_EventId_may_be_appended_again()
    {
        await using var chart = new ControlHarness(typeof(LiveChartNeuron), state: new LiveChartState { Points = [.. Enumerable.Range(0, 200).Select(i => Point($"e{i}", $"p{i}"))] });
        await chart.Send(new Append(Point("e200", "p200")));

        await chart.Send(new Append(Point("e0", "again")));

        var points = (await chart.Drawn<LiveChartView>()).Points;
        Assert.Equal("again", points[^1].Label);
        Assert.Equal(2, chart.Published().Length);
    }

    [Fact]
    public async Task Every_change_publishes_ChartChanged()
    {
        await using var chart = new ControlHarness(typeof(LiveChartNeuron));

        await chart.Send(new Replace([Point("a")]));
        await chart.Send(new Append(Point("b")));
        await chart.Send(new Replace([]));

        Assert.Equal([new ChartChanged(1), new ChartChanged(2), new ChartChanged(3)], chart.Published());
    }

    [Fact]
    public async Task Append_without_a_label_is_rejected()
    {
        await using var chart = new ControlHarness(typeof(LiveChartNeuron));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => chart.Send(new Append(Point("a", " "))));
    }
}

// The kind is the parent's prop, so the parent's routing hop refuses a command to a chart whose kind no chart can draw.
public sealed class LiveChartSlotFacts : IAsyncDisposable
{
    private static readonly BrainId Brain = new("local");
    private static readonly NeuronId ParentId = new(Brain, "test.chart-parent", "main");

    private readonly IDisposable _person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
    private readonly TestOS _os = TestOS.Local();

    public LiveChartSlotFacts()
    {
        _os.Register<ChartParent>();
        _os.Register<LiveChartNeuron>();
    }

    private Task Send<T>(string slot, string alias, T command) where T : Signal
        => _os.Get<IScreen>(ParentId).Dispatch(new Command(slot, alias, JsonSerializer.SerializeToElement(command, JsonSerializerOptions.Web)));

    [Fact]
    public async Task Unknown_kind_is_refused()
    {
        var replace = await Assert.ThrowsAsync<ActivationRefusedException>(() => Send("odd", "control.replace", new Replace([new LivePoint("a", "x", 1)])));
        var append = await Assert.ThrowsAsync<ActivationRefusedException>(() => Send("odd", "control.append", new Append(new LivePoint("a", "x", 1))));

        Assert.Equal(ChartRefusals.UnknownKind, replace.Reason);
        Assert.Equal(ChartRefusals.UnknownKind, append.Reason);
    }

    [Fact]
    public async Task A_known_kind_reaches_the_chart()
    {
        await Send("spend", "control.append", new Append(new LivePoint("a", "x", 1)));

        var chart = new NeuronId(Brain, "control.liveChart", ScreenExtensions.ChildAddress(ParentId, "spend"));
        Assert.Equal(new ChartChanged(1), Assert.Single(_os.Journal(Brain).Signals(chart).Select(entry => entry.Signal), signal => signal is not StateChanged && signal is not ViewChanged));
    }

    public async ValueTask DisposeAsync()
    {
        await _os.DisposeAsync();
        _person.Dispose();
    }
}
