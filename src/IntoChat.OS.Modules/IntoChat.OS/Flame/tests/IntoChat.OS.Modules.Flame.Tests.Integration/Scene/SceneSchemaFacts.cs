using IntoChat.OS.Modules.Flame.Scene;
using Xunit;

namespace IntoChat.OS.Modules.Flame.Tests.Integration.Scene;

public sealed class SceneSchemaFacts(FlameHost host)
{
    private const string Path = "apps/ino/scenes/home";

    private static SceneNode Node(string type, params (string Key, string Value)[] parameters) =>
        new("n", type, new Transform(), null, parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), [], [], []);

    private static async Task Set(IScene scene, SceneNode node, string[]? inputs = null)
    {
        var definition = new SceneDefinition([node with { Inputs = inputs ?? [] }], new SceneCamera());
        await scene.Set(definition, (await scene.Read()).Revision);
    }

    [Fact]
    public async Task AnUnknownNodeTypeIsStillAcceptedAsAPlaceholder()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var scene = brain.Get<IScene>(person.Key(Path));
        await scene.Set(new([Scenes.Node("x", "hologram")], new SceneCamera()), 0);
        Assert.Equal("hologram", (await scene.Read()).Definition.Nodes[0].Type);
    }

    [Fact]
    public async Task AnUnknownParameterKeyOnAKnownTypeIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var scene = brain.Get<IScene>(person.Key(Path));
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Set(scene, Node("sphere", ("icon", "ino"), ("modd", "idle"))));
        Assert.Contains("modd", error.Message);
    }

    [Fact]
    public async Task AKnownTypeMissingARequiredParameterIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var scene = brain.Get<IScene>(person.Key(Path));
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Set(scene, Node("text", ("size", "14"))));
        Assert.Contains("text", error.Message);
    }

    [Fact]
    public async Task AChoiceValueOutsideItsDomainIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var scene = brain.Get<IScene>(person.Key(Path));
        await Assert.ThrowsAsync<ArgumentException>(() => Set(scene, Node("sphere", ("icon", "ino"), ("state", "sleepy"))));
    }

    [Fact]
    public async Task AColorParameterThatIsNotAColorIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var scene = brain.Get<IScene>(person.Key(Path));
        await Assert.ThrowsAsync<ArgumentException>(() => Set(scene, Node("circle", ("color", "red"))));
    }

    [Fact]
    public async Task AGestureOutsideTheVocabularyIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var scene = brain.Get<IScene>(person.Key(Path));
        await Assert.ThrowsAsync<ArgumentException>(() => Set(scene, Node("circle"), ["swipe"]));
    }

    [Fact]
    public async Task AValidKnownSceneIsAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var scene = brain.Get<IScene>(person.Key(Path));
        await Set(scene, Node("sphere", ("icon", "ino"), ("state", "ready")), ["tap"]);
        var state = await scene.Read();
        Assert.Equal("ready", state.Definition.Nodes[0].Parameters["state"]);
    }

    [Fact]
    public async Task TheSphereAndActivityRingSceneShapesAreAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var scene = brain.Get<IScene>(person.Key(Path));
        var ring = new SceneNode("ring", "activity_ring", new Transform(), null,
            new() { ["items"] = "[{\"id\":\"r1\",\"label\":\"Routing\",\"kind\":\"route\",\"progress\":0.4}]", ["radius"] = "220" }, [], [], []);
        var graph = new SceneNode("sphere", "sphere", new Transform(), null,
            new() { ["icon"] = "hub", ["title"] = "Behavior runs" }, [], [], []);
        await scene.Set(new([ring, graph], new SceneCamera()), (await scene.Read()).Revision);
        Assert.Equal(2, (await scene.Read()).Definition.Nodes.Length);
    }

    [Fact]
    public async Task ATreeWhoseConditionNamesAnUndeclaredKeyIsRefusedAndThePreviousSceneStands()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var scene = brain.Get<IScene>(person.Key(Path));
        await scene.Set(Scenes.Ino(), 0);
        var sphere = new SceneNode("sphere", "sphere", new Transform(), null, new() { ["icon"] = "ino", ["blackboard"] = """{"mood":"idle"}""" }, [], [], [])
        { Tree = new BehaviorTree(TreeNode.Condition("unread", "1")) };
        var error = await Assert.ThrowsAsync<ArgumentException>(() => scene.Set(new([sphere], new SceneCamera()), 1));
        Assert.Contains("unread", error.Message);
        Assert.Equal(1, (await scene.Read()).Revision);
    }
}
