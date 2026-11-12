using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using IntoChat.OS.Modules.Flame.Scene;
using IntoChat.OS.Modules.Flame.Scene.Signals;

namespace IntoChat.OS.Modules.Flame.Tests;

// The plain scene neuron (06-06a). The Unit tier runs each fact on TestOS; the Integration tier runs them on both runtime kinds (R4-05).
public sealed class SceneNeuronFacts(FlameFixture fixture) : IClassFixture<FlameFixture>
{
    private static readonly BrainId Brain = new("facts");

    private static SceneDefinition OneNode() => new([new("orb", "circle", new Transform(), null, [], [], [], ["tap"])], new SceneCamera());

    [Fact]
    public async Task ASetMovesTheRevisionAndNamesTheSceneByItsFullKey()
    {
        var os = fixture.Runtime;
        var id = new NeuronId(Brain, FlameVocabulary.SceneType, "apps/ino/scenes/set-unit");
        var scene = os.Get<IScene>(id);

        await scene.Set(OneNode(), 0);
        await Assert.ThrowsAsync<RevisionConflictException>(() => scene.Set(OneNode(), 0));

        var read = await scene.Read();
        Assert.Equal(($"{id.Brain.Value}/{id.Key}", 1L), (read.Name, read.Revision));
        var changed = Assert.Single(os.Journal(Brain).Signals(id).Select(entry => entry.Signal).OfType<SceneChanged>());
        Assert.Equal(($"{id.Brain.Value}/{id.Key}", 1L), (changed.Name, changed.Revision));
    }

    [Fact]
    public async Task AReportOnAKnownNodePublishesInputWithoutMovingTheRevision()
    {
        var os = fixture.Runtime;
        var id = new NeuronId(Brain, FlameVocabulary.SceneType, "apps/ino/scenes/report-unit");
        var scene = os.Get<IScene>(id);
        await scene.Set(OneNode(), 0);

        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => scene.Report("missing", "tap", 1, 2, null));
        Assert.Contains("flame.scene-node-exists", ex.Rejected.Rules);
        await scene.Report("orb", "tap", 1, 2, "x");

        var input = Assert.Single(os.Journal(Brain).Signals(id).Select(entry => entry.Signal).OfType<SceneInput>());
        Assert.Equal(("orb", "tap", "x"), (input.NodeId, input.Kind, input.Payload));
        Assert.Equal(1, (await scene.Read()).Revision);
    }
}
