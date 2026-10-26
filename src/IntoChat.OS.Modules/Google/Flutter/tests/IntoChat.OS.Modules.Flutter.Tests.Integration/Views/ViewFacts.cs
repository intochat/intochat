using IntoChat.OS.Modules.Flutter.Views;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Views;

public sealed class ViewFacts(FlutterHost host)
{
    [Fact]
    public async Task PublicationOrdersVersionsAndSurvivesReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var view = brain.Get<IView>("views/views");
        Assert.Null(await view.Read());
        await view.Publish(new(1, 2, Ui.Text("title", "latest")));
        await view.Publish(new(1, 1, Ui.Text("title", "old")));
        await view.Publish(new(1, 2, Ui.Text("title", "latest")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => view.Publish(new(1, 2, Ui.Text("title", "conflict"))));
        await brain.DeactivateAsync(view, ct);
        Assert.Equal("latest", (await view.Read())!.Root.Text);
        await view.Publish(new(2, 1, Ui.Text("title", "new generation")));
        await view.Publish(new(1, 9, Ui.Text("title", "old generation")));
        Assert.Equal(2, (await view.Read())!.Generation);
    }

    // A watch opened after a publish (a reloaded page) still learns of it: the runtime retains the view's
    // saved ViewChanged signals (R4-18), the last one naming the newest generation and version.
    [Fact]
    public async Task TheLastChangeIsRetainedForAWatchOpenedLater()
    {
        var ct = TestContext.Current.CancellationToken;
        var view = host.Brain.Get<IView>("views/retained");
        Assert.Empty(await host.Brain.History<ViewChanged>(view, ct));
        await view.Publish(new(1, 2, Ui.Text("title", "first")));
        var first = Assert.Single(await host.Brain.History<ViewChanged>(view, ct));
        Assert.Equal((1L, 2L), (first.Generation, first.Version));
        Assert.EndsWith("retained", first.Name, StringComparison.Ordinal);
        await view.Publish(new(2, 1, Ui.Text("title", "second")));
        var last = (await host.Brain.History<ViewChanged>(view, ct))[^1];
        Assert.Equal((2L, 1L), (last.Generation, last.Version));
    }

    [Fact]
    public async Task MaximumDepthCanBePublishedIdempotently()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var view = brain.Get<IView>("views/deep");
        var root = Ui.Text("leaf", "x");
        for (var i = 0; i < 31; i++) { root = Ui.Column("level" + i, [root]); }
        await view.Publish(new(1, 1, root));
        await view.Publish(new(1, 1, root));
        Assert.NotNull(await view.Read());
    }

    [Fact]
    public async Task ActionsUseCanonicalTreeAndRejectStaleDisabledAndForgedRequests()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var view = brain.Get<IView>("views/actions");
        await using var actions = await brain.Observe<ViewAction>(view, ct);
        await view.Publish(new(3, 1, Ui.Column("root", [Ui.Button("go", "Go", "navigate"), Ui.Button("off", "Off", "delete", false)])));
        await Assert.ThrowsAsync<StaleActionException>(() => view.Activate(2, "go", "navigate", Guid.NewGuid()));
        await Assert.ThrowsAsync<StaleActionException>(() => view.Activate(3, "go", "delete", Guid.NewGuid()));
        await Assert.ThrowsAsync<StaleActionException>(() => view.Activate(3, "off", "delete", Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() => view.Activate(3, "go", "navigate", Guid.Empty));
        var request = Guid.NewGuid();
        await view.Activate(3, "go", "navigate", request);
        var action = await actions.NextAsync(ct: ct);
        Assert.Equal(("go", "navigate", request, 3L), (action.Key, action.Action, action.RequestId, action.Generation));
    }

    [Fact]
    public async Task ButtonsNestedInKitContainersActivate()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var view = brain.Get<IView>("views/kit-actions");
        await using var actions = await brain.Observe<ViewAction>(view, ct);
        await view.Publish(new(1, 1, Ui.Column("root",
        [
            Ui.Menu("menu", "More", [Ui.Button("rename", "Rename", "rename")]),
            Ui.Modal("modal", "Sure?", null, [Ui.Button("confirm", "Yes", "confirm")]),
            Ui.Toast("toast", "Saved", UiTone.Done, Ui.Button("undo", "Undo", "undo")),
        ])));
        foreach (var (key, action) in new[] { ("rename", "rename"), ("confirm", "confirm"), ("undo", "undo") })
        {
            var request = Guid.NewGuid();
            await view.Activate(1, key, action, request);
            var published = await actions.NextAsync(ct: ct);
            Assert.Equal((key, action, request), (published.Key, published.Action, published.RequestId));
        }
        await Assert.ThrowsAsync<StaleActionException>(() => view.Activate(1, "menu", "rename", Guid.NewGuid()));
    }
}
