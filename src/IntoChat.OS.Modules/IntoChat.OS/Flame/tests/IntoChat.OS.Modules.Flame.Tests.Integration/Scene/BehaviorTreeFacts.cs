using IntoChat.OS.Modules.Flame.Scene;
using Xunit;

namespace IntoChat.OS.Modules.Flame.Tests.Integration.Scene;

public sealed class BehaviorTreeFacts
{
    private static SceneDefinition Sphere(BehaviorTree? tree, string blackboard = """{"mood":"idle"}""") => new(
        [new("ino", "sphere", new Transform(), null, new() { ["icon"] = "ino", ["blackboard"] = blackboard }, [], [], ["tap"]) { Tree = tree }],
        new SceneCamera());

    [Fact]
    public void AValidPresenceTreeIsAccepted()
        => SceneValidation.Check(Sphere(new(TreeNode.Selector(
            TreeNode.Sequence(false, TreeNode.Condition("mood", "thinking"), TreeNode.Pulse(0.2, 1)),
            TreeNode.Sequence(false, TreeNode.Condition("running", "true"), TreeNode.Pulse(0.1, 2)),
            TreeNode.Repeat(TreeNode.Wait(1))))));
    [Fact]
    public void AConditionOnAnUndeclaredKeyIsRefused()
        => Assert.Contains("unread", Assert.Throws<ArgumentException>(() => SceneValidation.Check(Sphere(new(TreeNode.Condition("unread", "1"))))).Message);
    [Fact]
    public void AnUnknownTreeKindIsRefused()
        => Assert.Throws<ArgumentException>(() => SceneValidation.Check(Sphere(new(new TreeNode("spin", [])))));
    [Fact]
    public void ARepeatNeedsExactlyOneChild()
        => Assert.Throws<ArgumentException>(() => SceneValidation.Check(Sphere(new(new TreeNode(TreeKinds.Repeat, [])))));
    [Fact]
    public void ATaskOutOfRangeIsRefused()
        => Assert.Throws<ArgumentException>(() => SceneValidation.Check(Sphere(new(TreeNode.Fade(1.5, 1)))));
    [Fact]
    public void AnOversizedTreeIsRefused()
    {
        TreeNode deep = TreeNode.Wait(1);
        for (var i = 0; i < 17; i++) { deep = TreeNode.Repeat(deep); }
        Assert.Throws<ArgumentException>(() => SceneValidation.Check(Sphere(new(deep))));
        var wide = TreeNode.Selector([.. Enumerable.Range(0, 257).Select(_ => TreeNode.Wait(1))]);
        Assert.Throws<ArgumentException>(() => SceneValidation.Check(Sphere(new(wide))));
    }
    [Fact] public void ANullTreeIsAccepted() => SceneValidation.Check(Sphere(null));
    [Fact]
    public void CheckTreeIsPublicOnTheSchemaAndHonoursDeclaredKeys()
    {
        FlameSchema.CheckTree(new(TreeNode.Condition("unread", "1")), ["unread"]);
        Assert.Throws<ArgumentException>(() => FlameSchema.CheckTree(new(TreeNode.Condition("unread", "1")), []));
    }
}
