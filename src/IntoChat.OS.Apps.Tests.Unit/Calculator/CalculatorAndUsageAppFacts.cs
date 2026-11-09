using DigitalBrain.Testing;
using IntoChat.OS.Apps.Calculator;
using IntoChat.OS.Apps.Usage;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Calculator;

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
}
