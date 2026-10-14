using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flame.Scene;
using DigitalBrain.Flutter;
using System.Globalization;

public static class Ino
{
    public static string Key(DigitalBrainConnection brain) => brain.Setting("App")!;
    public static string Scope(DigitalBrainConnection brain) => brain.Setting("Brain") ?? Key(brain)[..Key(brain).IndexOf("/apps/", StringComparison.Ordinal)];
    public static string AssistantKey(DigitalBrainConnection brain) => Scope(brain) + "/apps/assistant";
    public static long ProgramRevision(DigitalBrainConnection brain) => long.Parse(brain.Setting("ProgramRevision")!, CultureInfo.InvariantCulture);

    public static BehaviorTree Presence() => new(TreeNode.Selector(
        TreeNode.Sequence(false, TreeNode.Condition("mood", "listening"), TreeNode.Scale(1.08, 0.4), TreeNode.Scale(1.0, 0.4)),
        TreeNode.Sequence(false, TreeNode.Condition("mood", "thinking"), TreeNode.Pulse(0.12, 1.0)),
        TreeNode.Sequence(false, TreeNode.Condition("mood", "acting"), TreeNode.Pulse(0.2, 0.5)),
        TreeNode.Sequence(false, TreeNode.Condition("mood", "speaking"), TreeNode.Fade(0.8, 0.3), TreeNode.Fade(1.0, 0.3)),
        TreeNode.Sequence(false, TreeNode.Condition("mood", "error"), TreeNode.Scale(0.9, 0.2), TreeNode.Wait(0.6), TreeNode.Scale(1.0, 0.2)),
        TreeNode.Repeat(TreeNode.Wait(1))));

    public static Presentation Presentation(DigitalBrainConnection brain)
        => new(new Sphere("icon.svg", ["mood"], Presence()), Side: null, Main: new UiChildRef(UIVocabulary.SurfaceType, AssistantKey(brain) + "/surface"), Views: [new("full", new UiChildRef(UIVocabulary.SurfaceType, AssistantKey(brain) + "/surface"))]);

    public static Task Present(DigitalBrainConnection brain) => brain.Get<IApp>(Key(brain)).Present(ProgramRevision(brain), Presentation(brain));

    public static string MoodOf(string activityState) => activityState switch
    {
        "listening" or "accepted" => "listening", "working" or "waiting" => "thinking",
        "reading" or "searching" => "acting", "completed" => "speaking", "attention" => "error", _ => "idle",
    };
}
