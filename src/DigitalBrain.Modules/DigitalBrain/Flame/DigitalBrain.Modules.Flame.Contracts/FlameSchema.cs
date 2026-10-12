using DigitalBrain;

namespace DigitalBrain.Flame;

public enum FlameParamKind { Text, Number, Color, Json, Choice, Boolean }

public sealed record FlameParam(string Key, FlameParamKind Kind, bool Required = false, string[]? Choices = null);

public sealed record FlameNodeType(string Type, FlameParam[] Parameters);

// The scene vocabulary as one schema: what each node type is and what parameters it takes. SceneValidation
// enforces it at Set; the renderer reads the same domains (a checked-in JSON mirrors this for Dart).
public static class FlameSchema
{
    public static readonly string[] Gestures = ["tap", "drag", "hover"];
    public static readonly string[] TweenProperties = ["x", "y", "scale", "rotation", "opacity"];
    public static readonly string[] TweenCurves = ["linear", "easeIn", "easeOut", "easeInOut"];
    public static readonly string[] Moods = ["idle", "listening", "thinking", "acting", "speaking", "error", "disconnected"];

    public static readonly FlameNodeType[] Nodes =
    [
        new("circle", [Number("radius"), Color("color")]),
        new("rect", [Number("width"), Number("height"), Color("color"), Number("corner")]),
        new("text", [Needed("text"), Number("size"), Color("color"), Choice("align", "left", "center", "right")]),
        new("sprite", [Needed("src"), Number("width"), Number("height")]),
        new("path", [Needed("d")]),
        new("particles", [Number("count"), Number("speed"), Color("color")]),
        new("group", []),
        new("ino_sphere", [Choice("mood", Moods), Number("radius"), Number("hue")]),
        new("brain_graph", [Choice("source", "events", "static"), Choice("filter", "apps", "all"), Text("selected"), Choice("layout", "sphere"), Text("title")]),
        new("activity_ring", [Json("items"), Number("radius")]),
    ];

    public static FlameNodeType? For(string type) => Nodes.FirstOrDefault(node => string.Equals(node.Type, type, StringComparison.Ordinal));

    private static FlameParam Number(string key) => new(key, FlameParamKind.Number);
    private static FlameParam Text(string key) => new(key, FlameParamKind.Text);
    private static FlameParam Needed(string key) => new(key, FlameParamKind.Text, Required: true);
    private static FlameParam Color(string key) => new(key, FlameParamKind.Color);
    private static FlameParam Json(string key) => new(key, FlameParamKind.Json);
    private static FlameParam Choice(string key, params string[] choices) => new(key, FlameParamKind.Choice, Choices: choices);
}
