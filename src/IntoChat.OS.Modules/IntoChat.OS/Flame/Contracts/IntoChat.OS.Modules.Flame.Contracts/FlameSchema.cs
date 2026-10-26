using IntoChat.OS;
using IntoChat.OS.Modules.Flame.Scene;

namespace IntoChat.OS.Modules.Flame;

public enum FlameParamKind { Text, Number, Color, Json, Choice, Boolean }

public sealed record FlameParam(string Key, FlameParamKind Kind, bool Required = false, string[]? Choices = null);

public sealed record FlameNodeType(string Type, FlameParam[] Parameters);

public sealed record TreeTask(string Kind, double MinSeconds, double MaxSeconds, double MinValue, double MaxValue);

// The scene vocabulary as one schema: what each node type is and what parameters it takes. SceneValidation
// enforces it at Set; the renderer reads the same domains (a checked-in JSON mirrors this for Dart).
public static class FlameSchema
{
    public static readonly string[] Gestures = ["tap", "drag", "hover"];
    public static readonly string[] TweenProperties = ["x", "y", "scale", "rotation", "opacity"];
    public static readonly string[] TweenCurves = ["linear", "easeIn", "easeOut", "easeInOut"];
    public static readonly string[] Moods = ["idle", "listening", "thinking", "acting", "speaking", "error", "disconnected"];

    public static readonly string[] ReservedBlackboardKeys = ["running", "attention"];

    // Range of each tree task: seconds, and the one value it carries (moveTo x and y share the position range).
    public static readonly TreeTask[] Tasks =
    [
        new(TreeKinds.Wait, 0, 60, 0, 0),
        new(TreeKinds.MoveTo, 0, 10, -4096, 4096),
        new(TreeKinds.Scale, 0, 10, 0.1, 4),
        new(TreeKinds.Fade, 0, 10, 0, 1),
        new(TreeKinds.Pulse, 0, 10, 0, 1),
    ];

    public static readonly FlameNodeType[] Nodes =
    [
        new("circle", [Number("radius"), Color("color")]),
        new("rect", [Number("width"), Number("height"), Color("color"), Number("corner")]),
        new("text", [Needed("text"), Number("size"), Color("color"), Choice("align", "left", "center", "right")]),
        new("sprite", [Needed("src"), Number("width"), Number("height")]),
        new("path", [Needed("d")]),
        new("particles", [Number("count"), Number("speed"), Color("color")]),
        new("group", []),
        new("activity_ring", [Json("items"), Number("radius")]),
        new("sphere", [Needed("icon"), Text("title"), Text("description"), Json("blackboard"), Json("side"), Json("main"), Json("toolbar"),
            Choice("state", "ready", "unready", "error"), Text("error"), Text("neuron"), Boolean("focused")]),
    ];

    // A tree is data the shell runs, so it is checked as strictly as parameters are: kinds, shape, size, ranges, and
    // that every condition reads a key the node declares (declaredKeys) or the shell always supplies.
    public static void CheckTree(BehaviorTree tree, string[] declaredKeys)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(declaredKeys);
        var count = 0;
        void Walk(TreeNode? node, int depth)
        {
            if (node is null) { throw new ArgumentException("A behavior tree node cannot be empty."); }
            if (++count > FlameVocabulary.MaxTreeNodes) { throw new ArgumentException($"A behavior tree holds at most {FlameVocabulary.MaxTreeNodes} nodes."); }
            if (depth > FlameVocabulary.MaxTreeDepth) { throw new ArgumentException($"A behavior tree is at most {FlameVocabulary.MaxTreeDepth} levels deep."); }
            if (!TreeKinds.All.Contains(node.Kind, StringComparer.Ordinal)) { throw new ArgumentException($"Unknown behavior tree kind '{node.Kind}'."); }
            var children = node.Children ?? [];
            var composite = TreeKinds.Composite.Contains(node.Kind, StringComparer.Ordinal);
            if (node.Kind == TreeKinds.Repeat && children.Length != 1) { throw new ArgumentException("A repeat node needs exactly one child."); }
            if (composite && children.Length == 0) { throw new ArgumentException($"A {node.Kind} node needs at least one child."); }
            if (!composite && children.Length != 0) { throw new ArgumentException($"A {node.Kind} node takes no children."); }
            if (node.Kind == TreeKinds.Condition)
            {
                if (string.IsNullOrWhiteSpace(node.Key) || node.Value is null) { throw new ArgumentException("A condition needs a key and a value."); }
                if (!ReservedBlackboardKeys.Contains(node.Key, StringComparer.Ordinal) && !declaredKeys.Contains(node.Key, StringComparer.Ordinal))
                { throw new ArgumentException($"A condition reads blackboard key '{node.Key}', which the node does not declare."); }
            }
            var task = Tasks.FirstOrDefault(candidate => candidate.Kind == node.Kind);
            if (task is not null)
            {
                InRange(node.Kind, "seconds", node.Seconds, task.MinSeconds, task.MaxSeconds);
                switch (node.Kind)
                {
                    case TreeKinds.MoveTo: InRange(node.Kind, "x", node.X, task.MinValue, task.MaxValue); InRange(node.Kind, "y", node.Y, task.MinValue, task.MaxValue); break;
                    case TreeKinds.Scale: InRange(node.Kind, "factor", node.Factor, task.MinValue, task.MaxValue); break;
                    case TreeKinds.Fade: InRange(node.Kind, "alpha", node.Alpha, task.MinValue, task.MaxValue); break;
                    case TreeKinds.Pulse: InRange(node.Kind, "amplitude", node.Amplitude, task.MinValue, task.MaxValue); break;
                }
            }
            foreach (var child in children) { Walk(child, depth + 1); }
        }
        Walk(tree.Root, 1);
    }

    private static void InRange(string kind, string field, double value, double min, double max)
    {
        if (double.IsNaN(value) || value < min || value > max) { throw new ArgumentException($"A {kind} node's {field} must be between {min} and {max}."); }
    }

    public static FlameNodeType? For(string type) => Nodes.FirstOrDefault(node => string.Equals(node.Type, type, StringComparison.Ordinal));

    private static FlameParam Number(string key) => new(key, FlameParamKind.Number);
    private static FlameParam Text(string key) => new(key, FlameParamKind.Text);
    private static FlameParam Needed(string key) => new(key, FlameParamKind.Text, Required: true);
    private static FlameParam Color(string key) => new(key, FlameParamKind.Color);
    private static FlameParam Boolean(string key) => new(key, FlameParamKind.Boolean);
    private static FlameParam Json(string key) => new(key, FlameParamKind.Json);
    private static FlameParam Choice(string key, params string[] choices) => new(key, FlameParamKind.Choice, Choices: choices);
}
