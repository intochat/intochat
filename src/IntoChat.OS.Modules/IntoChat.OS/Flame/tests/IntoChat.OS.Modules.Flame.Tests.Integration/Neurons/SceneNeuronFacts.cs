using DigitalBrain;
using DigitalBrain.Rules;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Flame.Scene;
using IntoChat.OS.Modules.Flame.Scene.Signals;

namespace IntoChat.OS.Modules.Flame.Tests;

// Integration tier: runs the scene neuron facts on both runtime kinds (R4-05).
public sealed class SceneNeuronFacts(FlameFixture fixture) : IClassFixture<FlameFixture>
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(20);

    public static TheoryData<NeuronRuntimeKind> Kinds => FlameFixture.Kinds;

    private static SceneDefinition OneNode() => new([new("orb", "circle", new Transform(), null, [], [], [], ["tap"])], new SceneCamera());

    [Theory, MemberData(nameof(Kinds))]
    public async Task ASetMovesTheRevisionAndNamesTheSceneByItsFullKey(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = await fixture.Runtime(kind);
        var id = runtime.Id(FlameVocabulary.SceneType, "apps/ino/scenes/set-" + kind);
        var scene = runtime.Get<IScene>(id);

        await scene.Set(OneNode(), 0);
        await Assert.ThrowsAsync<RevisionConflictException>(() => scene.Set(OneNode(), 0));

        var read = await scene.Read();
        Assert.Equal(($"{id.Brain.Value}/{id.Key}", 1L), (read.Name, read.Revision));
        var changed = Assert.Single(await runtime.WaitForSignals<SceneChanged>(id, 1, Within, ct));
        Assert.Equal(($"{id.Brain.Value}/{id.Key}", 1L), (changed.Name, changed.Revision));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task AReportOnAKnownNodePublishesInputWithoutMovingTheRevision(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = await fixture.Runtime(kind);
        var id = runtime.Id(FlameVocabulary.SceneType, "apps/ino/scenes/report-" + kind);
        var scene = runtime.Get<IScene>(id);
        await scene.Set(OneNode(), 0);

        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => scene.Report("missing", "tap", 1, 2, null));
        Assert.Contains("flame.scene-node-exists", ex.Rejected.Rules);
        await scene.Report("orb", "tap", 1, 2, "x");

        var input = Assert.Single(await runtime.WaitForSignals<SceneInput>(id, 1, Within, ct));
        Assert.Equal(("orb", "tap", "x"), (input.NodeId, input.Kind, input.Payload));
        Assert.Equal(1, (await scene.Read()).Revision);
    }
}
