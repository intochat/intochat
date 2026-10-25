using IntoChat.OS.Modules.Flame.Scene;

namespace IntoChat.OS.Modules.Flame.Tests.Unit;

public sealed class SceneValidationFacts
{
    private static SceneNode Node(string id, string type = "circle", Dictionary<string, string>? parameters = null,
        SceneNode[]? children = null, Tween[]? tweens = null, string[]? inputs = null)
        => new(id, type, new Transform(), null, parameters ?? [], children ?? [], tweens ?? [], inputs ?? []);

    private static SceneDefinition Scene(params SceneNode[] nodes) => new(nodes, new SceneCamera());

    [Fact]
    public void AValidNestedSceneIsAccepted()
        => SceneValidation.Check(Scene(
            Node("root", "group", children: [Node("dot", "circle", new() { ["radius"] = "4.5", ["color"] = "#ff00aa" }, inputs: ["tap"])]),
            Node("label", "text", new() { ["text"] = "hi", ["align"] = "center" }, tweens: [new("x", 0, 10, 300, "easeOut")])));

    [Fact]
    public void ABlankOrRepeatedNodeIdIsRefused()
    {
        Assert.Throws<ArgumentException>(() => SceneValidation.Check(Scene(Node(" "))));
        var twice = Assert.Throws<ArgumentException>(() => SceneValidation.Check(Scene(Node("a"), Node("g", "group", children: [Node("a")]))));
        Assert.Contains("'a'", twice.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("circle", "radius", "wide")]
    [InlineData("circle", "color", "red")]
    [InlineData("circle", "color", "#12345")]
    [InlineData("text", "align", "justify")]
    [InlineData("sphere", "focused", "maybe")]
    [InlineData("sphere", "blackboard", "{not json")]
    public void AParameterThatDoesNotParseAsItsKindIsRefused(string type, string key, string value)
    {
        var parameters = new Dictionary<string, string> { [key] = value };
        if (type == "text") { parameters["text"] = "x"; }
        if (type == "sphere") { parameters["icon"] = "ino"; }
        Assert.Throws<ArgumentException>(() => SceneValidation.Check(Scene(Node("n", type, parameters))));
    }

    [Fact]
    public void AnUnknownParameterAndAMissingRequiredOneAreRefused()
    {
        Assert.Throws<ArgumentException>(() => SceneValidation.Check(Scene(Node("n", "circle", new() { ["bogus"] = "1" }))));
        var missing = Assert.Throws<ArgumentException>(() => SceneValidation.Check(Scene(Node("n", "text"))));
        Assert.Contains("'text'", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownNodeTypeIsLeftAloneForOlderShells()
        => SceneValidation.Check(Scene(Node("n", "future-widget", new() { ["anything"] = "goes" })));

    [Fact]
    public void UnknownGesturesTweenPropertiesAndCurvesAreRefused()
    {
        Assert.Throws<ArgumentException>(() => SceneValidation.Check(Scene(Node("n", inputs: ["wiggle"]))));
        Assert.Throws<ArgumentException>(() => SceneValidation.Check(Scene(Node("n", tweens: [new("hue", 0, 1, 100)]))));
        Assert.Throws<ArgumentException>(() => SceneValidation.Check(Scene(Node("n", tweens: [new("x", 0, 1, 100, "bouncy")]))));
    }

    [Fact]
    public void TheNodeAndParameterByteBudgetsAreEnforced()
    {
        var tooMany = Enumerable.Range(0, FlameVocabulary.MaxNodes + 1).Select(index => Node("n" + index)).ToArray();
        Assert.Throws<ArgumentException>(() => SceneValidation.Check(Scene(tooMany)));
        var heavy = new Dictionary<string, string> { ["text"] = new string('x', FlameVocabulary.MaxParameterBytes + 1) };
        Assert.Throws<ArgumentException>(() => SceneValidation.Check(Scene(Node("n", "text", heavy))));
    }

    [Fact]
    public void ANullSceneIsRefused()
        => Assert.Throws<ArgumentNullException>(() => SceneValidation.Check(null!));
}
