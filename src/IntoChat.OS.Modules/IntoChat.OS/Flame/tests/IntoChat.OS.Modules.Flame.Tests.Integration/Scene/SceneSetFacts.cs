using IntoChat.OS.Modules.Flame.Scene;
using IntoChat.OS.Modules.Flame.Scene.Signals;
using Xunit;

namespace IntoChat.OS.Modules.Flame.Tests.Integration.Scene;

public sealed class SceneSetFacts(FlameHost host)
{
    private const string Path = "apps/ino/scenes/home";

    [Fact]
    public async Task SetPublishesChangedAndReadReturnsTheWholeGraph()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var scene = brain.Get<IScene>(person.Key(Path));
        await using var changes = await brain.Observe<SceneChanged>(scene, ct);
        await scene.Set(Scenes.Ino(), 0);
        var changed = await changes.NextAsync(ct: ct);
        Assert.Equal(person.Key(Path), changed.Name);
        Assert.Equal(1, changed.Revision);
        var state = await scene.Read();
        Assert.Equal("sphere", state.Definition.Nodes[0].Type);
        Assert.NotNull(state.Definition.Nodes[0].Tree);
    }

    [Fact]
    public async Task AStaleRevisionIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var scene = brain.Get<IScene>(person.Key(Path));
        await scene.Set(Scenes.Ino(), 0);
        await Assert.ThrowsAsync<RevisionConflictException>(() => scene.Set(Scenes.Ino(), 0));
        Assert.Equal(1, (await scene.Read()).Revision);
    }

    [Fact]
    public async Task ASetWithADuplicateNodeIdIsRefusedAndThePreviousDefinitionStands()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var scene = brain.Get<IScene>(person.Key(Path));
        await scene.Set(Scenes.Ino(), 0);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => scene.Set(new([Scenes.Node("a"), Scenes.Node("a")], new SceneCamera()), 1));
        Assert.Contains("'a'", error.Message);
        var state = await scene.Read();
        Assert.Equal(1, state.Revision);
        Assert.Equal("sphere", state.Definition.Nodes[0].Id);
    }

    [Fact]
    public async Task DuplicateNodeIdsAndTooManyNodesAreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var scene = brain.Get<IScene>(person.Key(Path));
        await Assert.ThrowsAsync<ArgumentException>(() => scene.Set(new([Scenes.Node("a"), Scenes.Node("a")], new()), 0));
        var many = Enumerable.Range(0, FlameVocabulary.MaxNodes + 1).Select(i => Scenes.Node("n" + i)).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() => scene.Set(new(many, new()), 0));
    }

    [Fact]
    public async Task AnUnknownNodeTypeIsAcceptedSoAnOlderShellCanStillShowAPlaceholder()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var scene = brain.Get<IScene>(person.Key(Path));
        await scene.Set(new([Scenes.Node("x", "hologram")], new()), 0);
        Assert.Equal("hologram", (await scene.Read()).Definition.Nodes[0].Type);
    }

    [Fact]
    public async Task TheGraphSurvivesReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var scene = brain.Get<IScene>(person.Key(Path));
        await scene.Set(Scenes.Ino(), 0);
        await brain.DeactivateAsync(scene, ct);
        var state = await scene.Read();
        Assert.Single(state.Definition.Nodes);
        Assert.Equal(TreeKinds.Selector, state.Definition.Nodes[0].Tree!.Root.Kind);
    }
}
