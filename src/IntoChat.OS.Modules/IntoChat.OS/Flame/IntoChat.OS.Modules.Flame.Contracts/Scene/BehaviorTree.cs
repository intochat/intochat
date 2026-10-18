using IntoChat.OS;

namespace IntoChat.OS.Flame.Scene;

public static class TreeKinds
{
    public const string Selector = "selector", Sequence = "sequence", Repeat = "repeat", Condition = "condition",
        Wait = "wait", MoveTo = "moveTo", Scale = "scale", Fade = "fade", Pulse = "pulse";
    public static readonly string[] All = [Selector, Sequence, Repeat, Condition, Wait, MoveTo, Scale, Fade, Pulse];
    public static readonly string[] Composite = [Selector, Sequence, Repeat];
}

// One closed shape for every node, as SceneNode is: the kind decides which fields mean something.
[GenerateSerializer, Alias("flame.tree-node")]
public sealed record TreeNode(
    [property: Id(0)] string Kind,
    [property: Id(1)] TreeNode[] Children,
    [property: Id(2)] string? Key = null,      // condition: blackboard key
    [property: Id(3)] string? Value = null,    // condition: expected value
    [property: Id(4)] double Seconds = 0,      // wait, moveTo, scale, fade, pulse
    [property: Id(5)] double X = 0,            // moveTo
    [property: Id(6)] double Y = 0,            // moveTo
    [property: Id(7)] double Factor = 1,       // scale
    [property: Id(8)] double Alpha = 1,        // fade
    [property: Id(9)] double Amplitude = 0,    // pulse
    [property: Id(10)] bool Memory = false)    // sequence: a running child is resumed on the next tick
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

[GenerateSerializer, Alias("flame.behavior-tree")]
public sealed record BehaviorTree([property: Id(0)] TreeNode Root);
