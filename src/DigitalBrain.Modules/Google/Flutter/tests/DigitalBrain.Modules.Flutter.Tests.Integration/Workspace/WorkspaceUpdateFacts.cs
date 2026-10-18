using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Workspace;

namespace DigitalBrain.Modules.Flutter.Tests.Integration.Workspace;

public sealed class WorkspaceUpdateFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task TypedViewsSurviveReactivationAndClosingTheLastLeavesNoFocus()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var scope = brain.Owner.BrainId;
        var workspace = brain.Get<IWorkspace>(scope);
        using var caller = brain.Owner.AsApp(scope + "/apps/workspace");
        WorkspaceViewRef[] views = [new("person", scope + "/apps/profile", "full", "person:alice"), new("document", scope + "/apps/files", "full", "document:123")];
        await workspace.Apply(new(views, "person", []), "open", 0);
        await brain.DeactivateAsync(workspace, ct);
        var restored = await workspace.Read();
        Assert.Equal(views, restored.OpenViews);
        Assert.Equal("person", restored.FocusedViewId);
        var empty = await workspace.Apply(new([], null, []), "close", restored.Revision);
        Assert.Empty(empty.OpenViews);
        Assert.Null(empty.FocusedViewId);
        Assert.Empty((await workspace.Read()).OpenViews);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ReplayedOperationIsBoundToItsPayloadAndStaleUpdatesCannotReopenViews()
    {
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(TestContext.Current.CancellationToken);
        var scope = brain.Owner.BrainId;
        var workspace = brain.Get<IWorkspace>(scope);
        using var caller = brain.Owner.AsApp(scope + "/apps/workspace");
        var open = new WorkspaceUpdate([new("a", scope + "/apps/files", "full", "")], "a", []);
        await workspace.Apply(open, "open", 0);
        Assert.Equal(1, (await workspace.Apply(open, "open", 0)).Revision);
        await Assert.ThrowsAsync<ArgumentException>(() => workspace.Apply(new([], null, []), "open", 0));
        await workspace.Apply(new([], null, []), "close", 1);
        await Assert.ThrowsAsync<RevisionConflictException>(() => workspace.Apply(open, "late", 1));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task OnlyTheWorkspaceAppCanApplyAndFocusMustNameAnOpenView()
    {
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(TestContext.Current.CancellationToken);
        var scope = brain.Owner.BrainId;
        var workspace = brain.Get<IWorkspace>(scope);
        using (brain.Owner.AsCaller())
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workspace.Apply(new([], null, []), "op", 0)); }
        using (brain.Owner.AsApp(scope + "/apps/workspace"))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => workspace.Apply(new([], "missing", []), "op", 0));
            await Assert.ThrowsAsync<ArgumentException>(() => workspace.Apply(new([new("a", "other/brain/apps/private", "full", "")], "a", []), "op", 0));
        }
    }
}
