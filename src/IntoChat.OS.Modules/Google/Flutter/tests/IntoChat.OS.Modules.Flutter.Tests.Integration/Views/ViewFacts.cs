using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Views;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Views;

public sealed class ViewFacts(FlutterHost host)
{
    [Fact]
    public async Task PublicationOrdersVersionsAndSurvivesReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var view = brain.Get<IView>("views");
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

    // A watch opened after a publish (a reloaded page) still learns of it: the view retains its last
    // ViewChanged, ordered by generation then version, and nothing for its actions.
    [Fact]
    public async Task TheLastChangeIsRetainedForAWatchOpenedLater()
    {
        var view = host.Brain.Get<IView>("retained");
        var retains = view.AsReference<IRetainsSignals>();
        Assert.Empty(await retains.Retained(nameof(ViewChanged), 0));
        await view.Publish(new(1, 2, Ui.Text("title", "first")));
        var first = Assert.Single(await retains.Retained(nameof(ViewChanged), 0));
        Assert.Equal((1L, 2L), (((ViewChanged)first.Signal).Generation, ((ViewChanged)first.Signal).Version));
        // Stamped like a published signal: a watch serializes its publisher.
        Assert.EndsWith("retained", first.Signal.Publisher.Name, StringComparison.Ordinal);
        await view.Publish(new(2, 1, Ui.Text("title", "second")));
        var second = Assert.Single(await retains.Retained(nameof(ViewChanged), 0));
        Assert.True(second.Sequence > first.Sequence);
        Assert.Equal(2L, ((ViewChanged)second.Signal).Generation);
        Assert.Empty(await retains.Retained(nameof(ViewChanged), second.Sequence));
        Assert.Empty(await retains.Retained(nameof(ViewAction), 0));
    }

    [Fact]
    public async Task MaximumDepthCanBePublishedIdempotently()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var view = brain.Get<IView>("deep");
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
        var view = brain.Get<IView>("actions");
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
        var view = brain.Get<IView>("kit-actions");
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
