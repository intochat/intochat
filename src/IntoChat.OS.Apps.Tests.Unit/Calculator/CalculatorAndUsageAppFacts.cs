using DigitalBrain.Testing;
using IntoChat.OS.Apps.Calculator;
using IntoChat.OS.Apps.Usage;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Calculator;

[DigitalBrain.Neuron("ui.compute"), DigitalBrain.StatelessNeuron]
public sealed class FakeComputeNeuron : DigitalBrain.Neuron, IntoChat.OS.Modules.Flutter.Compute.ICompute
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
    public static IEnumerable<TheoryDataRow<AppOperationCase>> CalculatorOperations
        => AppBaseline.Cases(typeof(CalculatorApp), os => os.Register(typeof(AppsModule).Assembly));

    public static IEnumerable<TheoryDataRow<AppOperationCase>> UsageOperations
        => AppBaseline.Cases(typeof(UsageApp), os => os.Register(typeof(AppsModule).Assembly));

    [Theory, MemberData(nameof(CalculatorOperations))]
    public Task CalculatorBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Theory, MemberData(nameof(UsageOperations))]
    public Task UsageBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Fact]
    public async Task CalculatorScenariosPass()
    {
        var report = await BddRunner.VerifyAsync<CalculatorApp>(
            fakes: os => os.Register(typeof(AppsModule).Assembly),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(report.Passed);
        Assert.Single(report.Scenarios);
    }

    [Fact]
    public async Task UsageScenariosPass()
    {
        var report = await BddRunner.VerifyAsync<UsageApp>(
            fakes: os => os.Register(typeof(AppsModule).Assembly),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(report.Passed);
        Assert.Single(report.Scenarios);
    }

    [Fact]
    public async Task UsageAndSettingsCanUpdateMonthlyBudgetAndReactToComputeChanges()
    {
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
