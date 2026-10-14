using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Viewport;
using DigitalBrain.Flutter.Workspace;

namespace DigitalBrain.Modules.Flutter.Tests.Composition;

public sealed class ViewportFacts
{
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
        await Assert.ThrowsAsync<RevisionConflictException>(() => viewport.Set(null, 0));
        await viewport.Set(null, 1);
        Assert.Null((await viewport.Read()).Target);
    }
}
