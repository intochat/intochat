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
        => ScreenBaseline.Cases(typeof(CalculatorApp), os => os.Register(typeof(AppsModule).Assembly));

    public static IEnumerable<TheoryDataRow<ScreenOperationCase>> UsageOperations
        => ScreenBaseline.Cases(typeof(UsageApp), os => os.Register(typeof(AppsModule).Assembly));

    [Theory, MemberData(nameof(CalculatorOperations))]
    public Task CalculatorBaseline(ScreenOperationCase operation) => ScreenBaseline.RunAsync(operation);

    [Theory, MemberData(nameof(UsageOperations))]
    public Task UsageBaseline(ScreenOperationCase operation) => ScreenBaseline.RunAsync(operation);

    [Fact]
    public async Task CalculatorScenariosPass()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("test", "test", new BrainId("test")));
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(CalculatorApp).Assembly);
        var screen = os.Get<IScreen>(new NeuronId(new BrainId("test"), "app.calculator", "default"));
        Assert.NotNull(await screen.Read());
    }

    [Fact]
    public async Task UsageScenariosPass()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("test", "test", new BrainId("test")));
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
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
                .Register(typeof(AppsModule).Assembly)
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

    [Fact]
    public async Task InoAndCreatorPauseExecutionWhenMonthlyComputeBudgetIsExceeded()
    {
        FakeComputeNeuron.Reset();
        try
        {
            await using var os = TestOS.Local()
                .Register(typeof(AppsModule).Assembly)
                .Register<FakeComputeNeuron>();
            var ino = await os.Install<IntoChat.OS.Apps.Ino.InoApp>();
            var creator = await os.Install<IntoChat.OS.Apps.Creator.CreatorApp>();

            FakeComputeNeuron.Budget = 5_000_000L;
            FakeComputeNeuron.Spent = 6_000_000L;

            await ino.Request("send", new IntoChat.OS.Apps.Ino.InoSend("Summarize revenue"));
            Assert.Contains("Monthly compute cap exceeded", ino.App.State.Status, StringComparison.Ordinal);

            await creator.Request("make", new IntoChat.OS.Apps.Creator.CreatorMake("Build a web watch unit"));
            Assert.Contains("Monthly compute cap exceeded", creator.App.State.Status, StringComparison.Ordinal);
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
