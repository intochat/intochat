using DigitalBrain.Testing;
using IntoChat.OS.Apps.FilmCraft;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.FilmCraft;

public sealed class FilmCraftAppFacts
{
    public static IEnumerable<TheoryDataRow<AppOperationCase>> FilmCraftOperations
        => AppBaseline.Cases(typeof(FilmCraftApp), os => os.Register(typeof(AppsModule).Assembly));

    [Theory, MemberData(nameof(FilmCraftOperations))]
    public Task FilmCraftStudioBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Fact]
    public async Task FilmCraftScenariosPass()
    {
        var report = await BddRunner.VerifyAsync<FilmCraftApp>(
            fakes: os => os.Register(typeof(AppsModule).Assembly),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(report.Passed);
        Assert.Single(report.Scenarios);
    }
}
