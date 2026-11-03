using DigitalBrain.Testing;
using IntoChat.OS.Apps.PhotoCraft;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.PhotoCraft;

public sealed class PhotoCraftAppFacts
{
    public static IEnumerable<TheoryDataRow<AppOperationCase>> PhotoCraftOperations
        => AppBaseline.Cases(typeof(PhotoCraftApp), os => os.Register(typeof(AppsModule).Assembly));

    [Theory, MemberData(nameof(PhotoCraftOperations))]
    public Task PhotoCraftStudioBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Fact]
    public async Task PhotoCraftScenariosPass()
    {
        var report = await BddRunner.VerifyAsync<PhotoCraftApp>(
            fakes: os => os.Register(typeof(AppsModule).Assembly),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(report.Passed);
        Assert.Single(report.Scenarios);
    }
}
