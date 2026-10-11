using DigitalBrain.Flame;
using DigitalBrain.Flame.Scene;
using DigitalBrain.Flame.Scene.Signals;
using Xunit;

namespace DigitalBrain.Modules.Flame.Tests.Scene;

public sealed class SceneFireFacts
{
    private const string Key = "owner/apps/ino/scenes/home";

    [Fact]
    public async Task FireMovesTheMachineAndPublishesEntered()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        await scene.Set(Scenes.Ino(), 0);
        await using var entered = await brain.Observe<SceneEntered>(scene, ct);
        await scene.Fire("think");
        var signal = await entered.NextAsync(ct: ct);
        Assert.Equal("presence", signal.Machine);
        Assert.Equal("thinking", signal.State);
        Assert.Equal("thinking", (await scene.Read()).Current["presence"]);
    }

    [Fact]
    public async Task AnEventNoTransitionListensToChangesNothingAndPublishesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        await scene.Set(Scenes.Ino(), 0);
        await using var entered = await brain.Observe<SceneEntered>(scene, ct);
        await scene.Fire("speak");   // idle has no "speak"
        Assert.Equal("idle", (await scene.Read()).Current["presence"]);
        var next = entered.NextAsync(ct: ct);
        Assert.NotSame(next, await Task.WhenAny(next, Task.Delay(TimeSpan.FromMilliseconds(300), ct)));
    }

    [Fact]
    public async Task TheCurrentStateSurvivesReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        await scene.Set(Scenes.Ino(), 0);
        await scene.Fire("think");
        await brain.DeactivateAsync(scene, ct);
        Assert.Equal("thinking", (await scene.Read()).Current["presence"]);
    }

    [Fact]
    public async Task ReportPublishesSceneInputWithTheNodeAndGesture()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        await scene.Set(Scenes.Ino(), 0);
        await using var inputs = await brain.Observe<SceneInput>(scene, ct);
        await scene.Report("sphere", "tap", 0.5, 0.5, null);
        var input = await inputs.NextAsync(ct: ct);
        Assert.Equal("sphere", input.NodeId);
        Assert.Equal("tap", input.Kind);
        Assert.Equal(Key, input.Name);
    }

    [Fact]
    public async Task ReportOnAnUnknownNodeIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        await scene.Set(Scenes.Ino(), 0);
        await Assert.ThrowsAsync<ArgumentException>(() => scene.Report("ghost", "tap", 0, 0, null));
    }
}
