using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Tree;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Tree;

public sealed class TreeFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task SetThenSelect()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .StartAsync(ct);
        var tree = brain.Get<ITree>("fs");
        await tree.Set([new TreeNode("root", null, "root"), new TreeNode("child", "root", "child")]);
        await tree.Select("child");
        Assert.Equal("child", (await tree.Read()).SelectedId);
    }
}
