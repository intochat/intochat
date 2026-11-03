using DigitalBrain.Testing;
using IntoChat.OS.Apps.Post;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Post;

public sealed class PostAppFacts
{
    public static IEnumerable<TheoryDataRow<AppOperationCase>> PostOperations
        => AppBaseline.Cases(typeof(PostApp), os => os.Register(typeof(AppsModule).Assembly));

    [Theory, MemberData(nameof(PostOperations))]
    public Task PostBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Fact]
    public async Task PostScenariosPass()
    {
        var report = await BddRunner.VerifyAsync<PostApp>(
            fakes: os => os.Register(typeof(AppsModule).Assembly),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(report.Passed);
        Assert.Single(report.Scenarios);
    }
}
