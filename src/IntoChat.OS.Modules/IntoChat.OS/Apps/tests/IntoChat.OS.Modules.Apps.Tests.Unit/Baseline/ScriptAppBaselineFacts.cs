using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Tests.Unit.Baseline;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// 08-08: Calculator stays a folder script and exercises the operation surface.
public sealed class ScriptAppBaselineFacts
{
    public static IEnumerable<TheoryDataRow<AppOperationCase>> CalculatorOperations => AppBaseline.Cases(typeof(CalculatorBaseline));

    [Theory]
    [MemberData(nameof(CalculatorOperations))]
    public Task CalculatorBaselineRuns(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Fact]
    public async Task AddOfTwoPlusThreeShowsFive()
    {
        await using var os = TestOS.Local();
        var installed = await os.Install<CalculatorBaseline>();
        await installed.Request("add", new CalculatorAdd("2+3"));
        Assert.Equal("5", installed.App.Display);
    }

    [Fact]
    public void EmptySumIsTheTypedError() => Assert.Equal("Error", CalculatorBaseline.Evaluate(""));
}
