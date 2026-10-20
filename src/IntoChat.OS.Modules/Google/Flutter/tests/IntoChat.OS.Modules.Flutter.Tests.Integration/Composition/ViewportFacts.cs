using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Viewport;
using IntoChat.OS.Flutter.Workspace;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Composition;

public sealed class ViewportFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task TargetIsDurableAndCannotCrossBrainsOrIgnoreContext()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var viewport = brain.Get<IViewport>("owner/a/apps/workspace/canvas");
        var target = new WorkspaceViewRef("calc", "owner/a/apps/calculator", "full", "");
        await viewport.Set(target, 0);
        await brain.DeactivateAsync(viewport, ct);
        Assert.Equal(target, (await viewport.Read()).Target);
        await Assert.ThrowsAsync<ArgumentException>(() => viewport.Set(target with { App = "owner/b/apps/private" }, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => viewport.Set(target with { Context = "unhandled" }, 1));
        // A viewport never shows its own app: that view could contain this viewport and recurse.
        await Assert.ThrowsAsync<ArgumentException>(() => viewport.Set(target with { App = "owner/a/apps/workspace" }, 1));
        await Assert.ThrowsAsync<RevisionConflictException>(() => viewport.Set(null, 0));
        await viewport.Set(null, 1);
        Assert.Null((await viewport.Read()).Target);
    }
}
