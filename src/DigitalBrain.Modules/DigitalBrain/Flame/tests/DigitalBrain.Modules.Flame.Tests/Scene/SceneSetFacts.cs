using DigitalBrain.Flame;
using DigitalBrain.Flame.Scene;
using DigitalBrain.Flame.Scene.Signals;
using Xunit;

namespace DigitalBrain.Modules.Flame.Tests.Scene;

public sealed class SceneSetFacts
{
    private const string Key = "owner/apps/ino/scenes/home";

    [Fact]
    public async Task SetPublishesChangedAndReadReturnsTheWholeGraphWithInitialStates()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        await using var changes = await brain.Observe<SceneChanged>(scene, ct);
        await scene.Set(Scenes.Ino(), 0);
        var changed = await changes.NextAsync(ct: ct);
        Assert.Equal(Key, changed.Name);
        Assert.Equal(1, changed.Revision);
        var state = await scene.Read();
        Assert.Equal("ino_sphere", state.Definition.Nodes[0].Type);
        Assert.Equal("idle", state.Current["presence"]);
    }

    [Fact]
    public async Task ARecomposeKeepsAMachinesCurrentStateWhenItStillExists()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        await scene.Set(Scenes.Ino(), 0);
        await scene.Fire("think");
        await scene.Set(Scenes.Ino(), 1);
        Assert.Equal("thinking", (await scene.Read()).Current["presence"]);
        await scene.Set(new([Scenes.Node("sphere")], [Scenes.Machine("presence", "idle", ["idle"])], new()), 2);
        Assert.Equal("idle", (await scene.Read()).Current["presence"]);
    }

    [Fact]
    public async Task AStaleRevisionIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        await scene.Set(Scenes.Ino(), 0);
        await Assert.ThrowsAsync<RevisionConflictException>(() => scene.Set(Scenes.Ino(), 0));
        Assert.Equal(1, (await scene.Read()).Revision);
    }

    [Fact]
    public async Task ASetWithAnUnknownTransitionStateIsRefusedAndThePreviousDefinitionStands()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        await scene.Set(Scenes.Ino(), 0);
        var broken = new SceneDefinition([Scenes.Node("a")],
            [Scenes.Machine("m", "idle", ["idle"], new Transition("idle", "go", "nowhere"))], new SceneCamera());
        var error = await Assert.ThrowsAsync<ArgumentException>(() => scene.Set(broken, 1));
        Assert.Contains("nowhere", error.Message);
        var state = await scene.Read();
        Assert.Equal(1, state.Revision);
        Assert.Equal("sphere", state.Definition.Nodes[0].Id);
    }

    [Fact]
    public async Task DuplicateNodeIdsAnUnknownInitialStateAndTooManyNodesAreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        await Assert.ThrowsAsync<ArgumentException>(() => scene.Set(new([Scenes.Node("a"), Scenes.Node("a")], [], new()), 0));
        await Assert.ThrowsAsync<ArgumentException>(() => scene.Set(new([Scenes.Node("a")], [Scenes.Machine("m", "gone", ["idle"])], new()), 0));
        var many = Enumerable.Range(0, FlameVocabulary.MaxNodes + 1).Select(i => Scenes.Node("n" + i)).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() => scene.Set(new(many, [], new()), 0));
    }

    [Fact]
    public async Task AnUnknownNodeTypeIsAcceptedSoAnOlderShellCanStillShowAPlaceholder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        await scene.Set(new([Scenes.Node("x", "hologram")], [], new()), 0);
        Assert.Equal("hologram", (await scene.Read()).Definition.Nodes[0].Type);
    }

    [Fact]
    public async Task TheGraphSurvivesReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        await scene.Set(Scenes.Ino(), 0);
        await brain.DeactivateAsync(scene, ct);
        var state = await scene.Read();
        Assert.Single(state.Definition.Nodes);
        Assert.Equal(4, state.Definition.Machines[0].Transitions.Length);
    }
}
