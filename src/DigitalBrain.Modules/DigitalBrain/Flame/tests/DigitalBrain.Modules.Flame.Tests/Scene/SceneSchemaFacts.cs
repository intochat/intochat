using DigitalBrain.Flame;
using DigitalBrain.Flame.Scene;
using Xunit;

namespace DigitalBrain.Modules.Flame.Tests.Scene;

public sealed class SceneSchemaFacts
{
    private const string Key = "owner/apps/ino/scenes/home";

    private static SceneNode Node(string type, params (string Key, string Value)[] parameters) =>
        new("n", type, new Transform(), null, parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), [], [], []);

    private static async Task Set(IScene scene, SceneNode node, string[]? inputs = null)
    {
        var definition = new SceneDefinition([node with { Inputs = inputs ?? [] }], [], new SceneCamera());
        await scene.Set(definition, (await scene.Read()).Revision);
    }

    [Fact]
    public async Task AnUnknownNodeTypeIsStillAcceptedAsAPlaceholder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        await scene.Set(new([Scenes.Node("x", "hologram")], [], new SceneCamera()), 0);
        Assert.Equal("hologram", (await scene.Read()).Definition.Nodes[0].Type);
    }

    [Fact]
    public async Task AnUnknownParameterKeyOnAKnownTypeIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Set(scene, Node("ino_sphere", ("modd", "idle"))));
        Assert.Contains("modd", error.Message);
    }

    [Fact]
    public async Task AKnownTypeMissingARequiredParameterIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Set(scene, Node("text", ("size", "14"))));
        Assert.Contains("text", error.Message);
    }

    [Fact]
    public async Task AChoiceValueOutsideItsDomainIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        await Assert.ThrowsAsync<ArgumentException>(() => Set(scene, Node("ino_sphere", ("mood", "sleepy"))));
    }

    [Fact]
    public async Task AColorParameterThatIsNotAColorIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        await Assert.ThrowsAsync<ArgumentException>(() => Set(scene, Node("circle", ("color", "red"))));
    }

    [Fact]
    public async Task AGestureOutsideTheVocabularyIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        await Assert.ThrowsAsync<ArgumentException>(() => Set(scene, Node("circle"), ["swipe"]));
    }

    [Fact]
    public async Task AValidKnownSceneIsAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        await Set(scene, Node("ino_sphere", ("mood", "thinking"), ("radius", "120")), ["tap"]);
        var state = await scene.Read();
        Assert.Equal("thinking", state.Definition.Nodes[0].Parameters["mood"]);
    }
}
