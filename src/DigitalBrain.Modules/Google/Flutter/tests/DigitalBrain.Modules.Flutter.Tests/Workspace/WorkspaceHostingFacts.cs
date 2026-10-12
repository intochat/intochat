using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Workspace;

namespace DigitalBrain.Modules.Flutter.Tests.Workspace;

public sealed class WorkspaceHostingFacts
{
    [Fact]
    public async Task ConcurrentDocumentsHaveOneWinnerAndTheRejectedOperationCanRetryAfterReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var workspace = brain.Get<IWorkspace>("owner/a");
        var requests = new[]
        {
            new OpenWindow("one", "one", "One", new("text", "owner/a/apps/editor/one"), 0),
            new OpenWindow("two", "two", "Two", new("text", "owner/a/apps/editor/two"), 0)
        };
        var attempts = requests.Select(workspace.Open).ToArray();
        await Assert.ThrowsAsync<WorkspaceRevisionConflictException>(() => Task.WhenAll(attempts));
        var winner = Assert.Single(attempts, attempt => attempt.IsCompletedSuccessfully);
        var rejected = Assert.Single(attempts, attempt => attempt.IsFaulted);
        var conflict = Assert.IsType<WorkspaceRevisionConflictException>(rejected.Exception!.InnerException);
        Assert.Equal(1, conflict.CurrentRevision);
        Assert.Single((await workspace.Read()).Windows);

        await brain.DeactivateAsync(workspace, ct);
        var retry = requests[Array.IndexOf(attempts, rejected)] with { ExpectedRevision = 1 };
        var applied = await workspace.Open(retry);
        Assert.Equal(2, applied.AppliedRevision);
        Assert.Equal(2, applied.State.Windows.Length);
        Assert.Equal(retry.WindowId, applied.State.FocusedWindow);

        await workspace.Focus(null, "home", 2);
        await brain.DeactivateAsync(workspace, ct);
        var replay = await workspace.Open(requests[Array.IndexOf(attempts, winner)]);
        Assert.Equal(1, replay.AppliedRevision);
        Assert.Equal(3, replay.State.Revision);
        Assert.Null(replay.State.FocusedWindow);
        Assert.All(replay.State.Windows, window => Assert.True(window.IsOpen));
    }

    [Fact]
    public async Task PresentationRootsAndSourceRevisionsSurviveReactivationWithoutChangingFocus()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var workspace = brain.Get<IWorkspace>("owner/a");
        var home = new UiChildRef("scene", "owner/a/apps/ino/home");
        var companion = new UiChildRef("surface", "owner/a/apps/assistant/surface");
        await workspace.Configure(home, companion, 0);
        await workspace.Open(new("one", "a", "A", new("text", "owner/a/a"), 1));
        Assert.Equal(3, (await workspace.SetConnectedSources(["windows"])).Revision);
        await brain.DeactivateAsync(workspace, ct);
        var state = await workspace.Read();
        Assert.Equal(home, state.Home);
        Assert.Equal(companion, state.Companion);
        Assert.Equal("a", state.FocusedWindow);
        await Assert.ThrowsAsync<ArgumentException>(() => workspace.Configure(new("text", "owner/b/home"), companion, 3));
        Assert.Null((await workspace.Focus(null, "home", 3)).FocusedWindow);
    }

    [Fact]
    public async Task TypedRootsShareOneFocusAuthorityAndReplayCannotStealFocus()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var workspace = brain.Get<IWorkspace>("owner/a");
        var first = new OpenWindow("one", "text", "Answer", new("text", "owner/a/apps/word/answer"), 0);
        await workspace.Open(first);
        await workspace.Open(new("two", "scene", "Ino", new("scene", "owner/a/apps/ino/home"), 1));
        Assert.Equal("scene", (await workspace.Open(first)).State.FocusedWindow);
        await workspace.Close("scene", 2, "close-scene");
        Assert.Null((await workspace.Read()).FocusedWindow);
        await workspace.Open(new("three", "scene", "Ino", new("scene", "owner/a/apps/ino/home"), 3));
        await brain.DeactivateAsync(workspace, ct);
        var replay = await workspace.Close("scene", 2, "close-scene");
        Assert.Equal("scene", replay.FocusedWindow);
        Assert.True(replay.Windows.Single(w => w.Id == "scene").IsOpen);
    }

    [Fact]
    public async Task FocusReceiptsCannotRestoreOldNavigationAndRejectConflictingReuse()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var workspace = brain.Get<IWorkspace>("owner/a");
        await workspace.Open(new("one", "a", "A", new("text", "owner/a/a"), 0));
        await workspace.Open(new("two", "b", "B", new("text", "owner/a/b"), 1));
        await workspace.Focus("a", "focus-a", 2);
        await workspace.Focus("b", "focus-b", 3);
        Assert.Equal("b", (await workspace.Focus("a", "focus-a", 2)).FocusedWindow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.Focus("b", "focus-a", 4));
        await Assert.ThrowsAsync<ArgumentException>(() => workspace.Open(new("foreign", "c", "C", new("scene", "owner/b/home"), 4)));
    }
}
