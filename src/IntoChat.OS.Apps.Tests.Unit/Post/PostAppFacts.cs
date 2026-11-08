using DigitalBrain.Testing;
using IntoChat.OS.Apps.Friends;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Friends;

public sealed class FriendsAppFacts
{
    public static IEnumerable<TheoryDataRow<AppOperationCase>> FriendsOperations
        => AppBaseline.Cases(typeof(FriendsApp), os => os.Register(typeof(AppsModule).Assembly));

    [Theory, MemberData(nameof(FriendsOperations))]
    public Task FriendsBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Fact]
    public async Task FriendsScenariosPass()
    {
        var report = await BddRunner.VerifyAsync<FriendsApp>(
            fakes: os => os.Register(typeof(AppsModule).Assembly),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(report.Passed);
        Assert.Single(report.Scenarios);
    }
}
