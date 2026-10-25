using IntoChat.OS.Modules.Flutter.Workspace;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Workspace;

public sealed class WorkspaceFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task AFreshBrainLandsOnIno()
    {
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(TestContext.Current.CancellationToken);
        var state = await brain.Get<IWorkspace>("owner/a").Read();
        Assert.Equal(0, state.Revision);
        Assert.Equal(["owner/a", "owner/a/apps/ino"], state.Path);
        Assert.Equal(["owner/a/apps/ino"], state.Open);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ShowPushesAndBackPops()
    {
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(TestContext.Current.CancellationToken);
        var workspace = brain.Get<IWorkspace>("owner/a");
        var shown = await workspace.Show("owner/a/apps/files", "op1", 0);
        Assert.Equal("owner/a/apps/files", shown.Path[^1]);
        Assert.Contains("owner/a/apps/files", shown.Open);
        Assert.Equal(1, shown.Revision);
        Assert.Equal("owner/a/apps/ino", (await workspace.Back("op2", 1)).Path[^1]);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task BackAtHomeAndHomeAtHomeAreNoOps()
    {
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(TestContext.Current.CancellationToken);
        var workspace = brain.Get<IWorkspace>("owner/a");
        Assert.Equal(["owner/a"], (await workspace.Home("h1", 0)).Path);
        var back = await workspace.Back("b1", 1);
        Assert.Equal(["owner/a"], back.Path);
        Assert.Equal(2, back.Revision);
        var home = await workspace.Home("h2", 2);
        Assert.Equal(["owner/a"], home.Path);
        Assert.Equal(3, home.Revision);
        Assert.Equal(3, (await workspace.Home("h2", 2)).Revision);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ClosingTheFocusedAppPopsThePath()
    {
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(TestContext.Current.CancellationToken);
        var workspace = brain.Get<IWorkspace>("owner/a");
        await workspace.Show("owner/a/apps/files", "show", 0);
        await workspace.Show("owner/a/apps/files/folder", "nested", 1);
        var closed = await workspace.Close("owner/a/apps/files", "close", 2);
        Assert.Equal("owner/a/apps/ino", closed.Path[^1]);
        Assert.DoesNotContain("owner/a/apps/files", closed.Open);
        Assert.DoesNotContain(closed.Path, key => key.StartsWith("owner/a/apps/files", StringComparison.Ordinal));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AStaleRevisionIsRefusedAndAReplayIsIdempotent()
    {
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(TestContext.Current.CancellationToken);
        var workspace = brain.Get<IWorkspace>("owner/a");
        await workspace.Show("owner/a/apps/files", "op", 0);
        Assert.Equal(1, (await workspace.Show("owner/a/apps/files", "op", 0)).Revision);
        Assert.Equal(1, (await Assert.ThrowsAsync<RevisionConflictException>(() => workspace.Show("owner/a/apps/memory", "op2", 0))).CurrentRevision);
    }

    [Trait("host", "own")]
    [Theory]
    [InlineData("other/b/apps/x")]
    [InlineData("owner/ab/apps/x")]
    [InlineData("owner/a/../other")]
    public async Task AKeyOutsideTheBrainIsRefused(string key)
    {
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentException>(() => brain.Get<IWorkspace>("owner/a").Show(key, "op", 0));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ThePathSurvivesReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var workspace = brain.Get<IWorkspace>("owner/a");
        await workspace.Show("owner/a/apps/files", "op", 0);
        await brain.DeactivateAsync(workspace, ct);
        Assert.Equal("owner/a/apps/files", (await workspace.Read()).Path[^1]);
    }
}
