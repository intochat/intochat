using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Views;
using Xunit;

namespace DigitalBrain.Modules.Flutter.Tests.Integration.Views;

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
}
