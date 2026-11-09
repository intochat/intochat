using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Apps.Friends;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Friends;

public sealed class FriendsAppFacts
{
    public static IEnumerable<TheoryDataRow<AppOperationCase>> FriendsOperations
        => AppBaseline.Cases(typeof(FriendsApp), os => os.Register(typeof(AppsModule).Assembly).Register<CreatorSession>());

    [Theory, MemberData(nameof(FriendsOperations))]
    public Task FriendsBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Fact]
    public async Task FriendsScenariosPass()
    {
        var report = await BddRunner.VerifyAsync<FriendsApp>(
            fakes: os => os.Register(typeof(AppsModule).Assembly).Register<CreatorSession>(),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(report.Passed);
        Assert.Single(report.Scenarios);
    }

    [Fact]
    public async Task ForkPost_PromotesTemplateSourceIntoCreatorSession()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register<CreatorSession>();

        var friends = await os.Install<FriendsApp>();
        await friends.Request("publish", new FriendsPublish(
            Content: "Interactive analytics board",
            Scope: "friends",
            Id: "board-1",
            Title: "Analytics Board",
            TemplateAppId: "friends.post.board-1",
            EmbeddedAppIds: "markdraw,data"));

        await friends.Request("fork-post", new FriendsForkPost("board-1", "board-1-remix", "Remix · Analytics Board"));

        var session = await os.Get<ICreatorSession>(new NeuronId(new BrainId("local"), "os.creator.session", "session")).Read();
        Assert.Equal("friends.post.board-1", session.TargetApp);
        Assert.Contains("PostTemplateApp", session.GeneratedSource, StringComparison.Ordinal);
    }
}
