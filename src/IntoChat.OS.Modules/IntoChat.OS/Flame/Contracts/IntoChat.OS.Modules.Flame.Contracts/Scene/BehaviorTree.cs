using IntoChat.OS;

namespace IntoChat.OS.Modules.Flame.Scene;

public static class TreeKinds
{
    public const string Selector = "selector", Sequence = "sequence", Repeat = "repeat", Condition = "condition",
        Wait = "wait", MoveTo = "moveTo", Scale = "scale", Fade = "fade", Pulse = "pulse";
    public static readonly string[] All = [Selector, Sequence, Repeat, Condition, Wait, MoveTo, Scale, Fade, Pulse];
    public static readonly string[] Composite = [Selector, Sequence, Repeat];
}

// One closed shape for every node, as SceneNode is: the kind decides which fields mean something.
public sealed record TreeNode(
    string Kind,
    TreeNode[] Children,
    string? Key = null,      // condition: blackboard key
    string? Value = null,    // condition: expected value
    double Seconds = 0,      // wait, moveTo, scale, fade, pulse
    double X = 0,            // moveTo
    double Y = 0,            // moveTo
    double Factor = 1,       // scale
    double Alpha = 1,        // fade
    double Amplitude = 0,    // pulse
    bool Memory = false)    // sequence: a running child is resumed on the next tick
{
    public static TreeNode Selector(params TreeNode[] children) => new(TreeKinds.Selector, children);
    public static TreeNode Sequence(bool memory, params TreeNode[] children) => new(TreeKinds.Sequence, children, Memory: memory);
    public static TreeNode Repeat(TreeNode child) => new(TreeKinds.Repeat, [child]);
    public static TreeNode Condition(string key, string value) => new(TreeKinds.Condition, [], key, value);
    public static TreeNode Wait(double seconds) => new(TreeKinds.Wait, [], Seconds: seconds);
    public static TreeNode MoveTo(double x, double y, double seconds) => new(TreeKinds.MoveTo, [], Seconds: seconds, X: x, Y: y);
    public static TreeNode Scale(double factor, double seconds) => new(TreeKinds.Scale, [], Seconds: seconds, Factor: factor);
    public static TreeNode Fade(double alpha, double seconds) => new(TreeKinds.Fade, [], Seconds: seconds, Alpha: alpha);
    public static TreeNode Pulse(double amplitude, double seconds) => new(TreeKinds.Pulse, [], Seconds: seconds, Amplitude: amplitude);
}

public sealed record BehaviorTree(TreeNode Root);
