using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Tests.Unit.Baseline;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// 08-08: Calculator, FilmCraft and PhotoCraft stay folder scripts. These twins have no AppBundle,
// so the catalogue keeps the folders. The baseline exercises the operation surface.
public sealed class ScriptAppBaselineFacts
{
    public static IEnumerable<TheoryDataRow<AppOperationCase>> CalculatorOperations => AppBaseline.Cases(typeof(CalculatorBaseline));

    public static IEnumerable<TheoryDataRow<AppOperationCase>> FilmCraftOperations => AppBaseline.Cases(typeof(FilmCraftBaseline));

    public static IEnumerable<TheoryDataRow<AppOperationCase>> PhotoCraftOperations => AppBaseline.Cases(typeof(PhotoCraftBaseline));

    [Theory]
    [MemberData(nameof(CalculatorOperations))]
    public Task CalculatorBaselineRuns(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Theory]
    [MemberData(nameof(FilmCraftOperations))]
    public Task FilmCraftBaselineRuns(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Theory]
    [MemberData(nameof(PhotoCraftOperations))]
    public Task PhotoCraftBaselineRuns(AppOperationCase operation) => AppBaseline.RunAsync(operation);

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
