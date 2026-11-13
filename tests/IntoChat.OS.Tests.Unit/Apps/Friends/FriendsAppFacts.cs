using DigitalBrain;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Friends;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Friends;

public sealed class FriendsAppFacts
{
    public static IEnumerable<TheoryDataRow<ScreenOperationCase>> FriendsOperations
        => ScreenBaseline.Cases(typeof(FriendsApp), os => os.Register(typeof(AppsModule).Assembly));

    [Theory, MemberData(nameof(FriendsOperations))]
    public Task FriendsBaseline(ScreenOperationCase operation) => ScreenBaseline.RunAsync(operation);

    [Fact]
    public async Task FriendsScenariosPass()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(FriendsApp).Assembly);
        var screen = os.Get<IScreen>(new NeuronId(new BrainId("test"), "app.friends", "default"));
        Assert.NotNull(await screen.Read());
    }

    [Fact]
    public async Task MultipleAppPostsRenderUniqueButtonKeysAndValidViewportUris()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var friends = await os.Install<FriendsApp>();

        await friends.Request("publish", new FriendsPublish("First post", "friends", "post-a", "First", "unit-a", "markdraw"));
        await friends.Request("publish", new FriendsPublish("Second post", "friends", "post-b", "Second", "unit-b", "data"));

        var view = friends.App.Render();
        var buttons = Nodes(view).OfType<Button>().ToArray();
        Assert.Contains(buttons, b => b.Key == "fork-app-template:post-a");
        Assert.Contains(buttons, b => b.Key == "fork-app-template:post-b");
        Assert.Contains(buttons, b => b.Key == "open-in-creator:post-a");
        Assert.Contains(buttons, b => b.Key == "open-in-creator:post-b");

        var refs = Nodes(view).OfType<NeuronRef>().ToArray();
        Assert.NotEmpty(refs);
        var json = System.Text.Json.JsonSerializer.Serialize(refs);
        Assert.Contains("local/apps/markdraw", json, StringComparison.Ordinal);
        Assert.Contains("local/apps/data", json, StringComparison.Ordinal);
    }

    private static IEnumerable<Node> Nodes(object? value) => value switch
    {
        null or string => [],
        Node node => [node, .. node.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).SelectMany(p => Nodes(p.GetValue(node)))],
        View view => Nodes(view.Root),
        System.Collections.IEnumerable items => items.Cast<object?>().SelectMany(Nodes),
        _ => [],
    };
}
