using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Calculator;
using IntoChat.OS.Apps.Usage;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Calculator;

[DigitalBrain.Neurons.Neuron("ui.compute"), DigitalBrain.Neurons.StatelessNeuron]
public sealed class FakeComputeNeuron : DigitalBrain.Neurons.Neuron, IntoChat.OS.Modules.Flutter.Compute.ICompute
{
    public static long Spent { get; set; }
    public static long Budget { get; set; } = IntoChat.OS.Modules.Flutter.Compute.ComputeState.DefaultMonthlyBudgetMicros;
    public static List<IntoChat.OS.Modules.Flutter.Compute.ComputeRow> Rows { get; } = [];

    public static void Reset()
    {
        Spent = 0;
        Budget = IntoChat.OS.Modules.Flutter.Compute.ComputeState.DefaultMonthlyBudgetMicros;
        Rows.Clear();
    }

    public Task<IntoChat.OS.Modules.Flutter.Compute.ComputeState> Read()
        => Task.FromResult(new IntoChat.OS.Modules.Flutter.Compute.ComputeState(Spent, DateTimeOffset.UtcNow, [.. Rows], Budget));

    public Task<IntoChat.OS.Modules.Flutter.Compute.ComputeState> Record(string key, long micros)
    {
        Spent += micros;
        Rows.Add(new IntoChat.OS.Modules.Flutter.Compute.ComputeRow(key, micros, 1));
        return Read();
    }

    public Task<IntoChat.OS.Modules.Flutter.Compute.ComputeState> SetBudget(long monthlyBudgetMicros)
    {
        Budget = monthlyBudgetMicros;
        return Read();
    }
}

public sealed class CalculatorAndUsageAppFacts
{
    public static IEnumerable<TheoryDataRow<ScreenOperationCase>> CalculatorOperations
        => ScreenBaseline.Cases(typeof(CalculatorApp), os => os.RegisterCatalog());

    public static IEnumerable<TheoryDataRow<ScreenOperationCase>> UsageOperations
        => ScreenBaseline.Cases(typeof(UsageApp), os => os.RegisterCatalog());

    [Theory, MemberData(nameof(CalculatorOperations))]
    public Task CalculatorBaseline(ScreenOperationCase operation) => ScreenBaseline.RunAsync(operation);

    [Theory, MemberData(nameof(UsageOperations))]
    public Task UsageBaseline(ScreenOperationCase operation) => ScreenBaseline.RunAsync(operation);

    [Fact]
    public async Task CalculatorScenariosPass()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("test", "test", new BrainId("test")));
        await using var os = TestOS.Local();
        os.RegisterCatalog();
        os.Register(typeof(CalculatorApp).Assembly);
        var screen = os.Get<IScreen>(new NeuronId(new BrainId("test"), "app.calculator", "default"));
        Assert.NotNull(await screen.Read());
    }

    [Fact]
    public async Task UsageScenariosPass()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("test", "test", new BrainId("test")));
        await using var os = TestOS.Local();
        os.RegisterCatalog();
        os.Register(typeof(UsageApp).Assembly);
        var screen = os.Get<IScreen>(new NeuronId(new BrainId("test"), "app.usage", "default"));
        Assert.NotNull(await screen.Read());
    }

    [Fact]
    public async Task UsageAndSettingsCanUpdateMonthlyBudgetAndReactToComputeChanges()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        FakeComputeNeuron.Reset();
        try
        {
            await using var os = TestOS.Local()
                .RegisterCatalog()
                .Register<FakeComputeNeuron>();
            var usage = await os.Install<UsageApp>();
            var settings = await os.Install<IntoChat.OS.Apps.Settings.SettingsApp>();

            await usage.Request("set-budget", new UsageSetBudget(10_000_000L));
            Assert.Equal(10_000_000L, usage.App.State.MonthlyBudgetMicros);
            Assert.False(usage.App.State.BudgetExceeded);

            var compute = os.Get<IntoChat.OS.Modules.Flutter.Compute.ICompute>(
                new DigitalBrain.NeuronId(new DigitalBrain.BrainId("local"), "ui.compute", "compute"));
            await compute.Record("ino/chat:gpt-5", 12_500_000L);
            await usage.Request("refresh", new UsageRefresh());

            Assert.Equal(12_500_000L, usage.App.State.SpentMicros);
            Assert.True(usage.App.State.BudgetExceeded);

            await settings.Request("set-budget", new IntoChat.OS.Apps.Settings.SettingsSetBudget(200_000_000L));
            await usage.Request("refresh", new UsageRefresh());
            Assert.Equal(200_000_000L, usage.App.State.MonthlyBudgetMicros);
            Assert.False(usage.App.State.BudgetExceeded);
        }
        finally
        {
            FakeComputeNeuron.Reset();
        }
    }

    // The spend chart is a `chart` data node drawn from Usage's own state; no ui.chart neuron is mounted (step P-Data/Usage).
    [Fact]
    public async Task UsageDrawsItsSpendChartFromState()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        FakeComputeNeuron.Reset();
        try
        {
            await using var os = TestOS.Local()
                .RegisterCatalog()
                .Register<FakeComputeNeuron>();
            var usage = await os.Install<UsageApp>();
            Assert.Empty(Find<DigitalBrain.UI.Chart>(usage.App.Render().Root));

            var compute = os.Get<IntoChat.OS.Modules.Flutter.Compute.ICompute>(
                new DigitalBrain.NeuronId(new DigitalBrain.BrainId("local"), "ui.compute", "compute"));
            await compute.Record("ino/chat:gpt-5", 12_500_000L);
            await compute.Record("creator/image", 1_250_000L);
            await usage.Request("refresh", new UsageRefresh());

            var root = usage.App.Render().Root;
            var chart = Assert.Single(Find<DigitalBrain.UI.Chart>(root));
            Assert.Equal("columns", chart.Kind);
            Assert.Equal("Spend by Surface (USD)", chart.Title);
            Assert.Equal([new DigitalBrain.UI.ChartPointNode("ino/chat:gpt-5", 12.5), new DigitalBrain.UI.ChartPointNode("creator/image", 1.25)], chart.Points);
            Assert.Equal("$", chart.Prefix);
            Assert.Equal("Compute", chart.From);
            Assert.Empty(Find<DigitalBrain.UI.NeuronRef>(root));
        }
        finally
        {
            FakeComputeNeuron.Reset();
        }
    }

    private static IEnumerable<T> Find<T>(object? value) where T : DigitalBrain.UI.Node => value switch
    {
        null or string => [],
        T match => [match, .. match.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).SelectMany(p => Find<T>(p.GetValue(match)))],
        System.Collections.IEnumerable items => items.Cast<object?>().SelectMany(Find<T>),
        DigitalBrain.UI.Node or DigitalBrain.UI.View => value.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).SelectMany(p => Find<T>(p.GetValue(value))),
        _ => [],
    };

    [Fact]
    public async Task InoAndCreatorPauseExecutionWhenMonthlyComputeBudgetIsExceeded()
    {
        FakeComputeNeuron.Reset();
        try
        {
            await using var os = TestOS.Local()
                .RegisterCatalog()
                .Register<FakeComputeNeuron>();
            var ino = await os.Install<IntoChat.OS.Apps.Ino.InoApp>();

            FakeComputeNeuron.Budget = 5_000_000L;
            FakeComputeNeuron.Spent = 6_000_000L;

            await ino.Request("send", new IntoChat.OS.Apps.Ino.InoSend("Summarize revenue"));
            Assert.Contains("Monthly compute cap exceeded", ino.App.State.Status, StringComparison.Ordinal);
        }
        finally
        {
            FakeComputeNeuron.Reset();
        }
    }

    [Fact]
    public void TraceReaderRedactsExtendedSecretFormats()
    {
        var raw = "key=AIzaSyD1234567890abcdef1234567890 token=xoxb-1234567890-abcdef123456 uri=vault://OPENAI_API_KEY";
        var redacted = IntoChat.OS.Modules.Apps.Trace.TraceReaderNeuron.Redact(raw);
        Assert.DoesNotContain("AIzaSy", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb-", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("vault://OPENAI_API_KEY", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }
}
