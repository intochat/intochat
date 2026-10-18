using IntoChat.OS.Flame.Scene;

namespace IntoChat.OS.Modules.Flame.Tests.Integration;

internal static class Scenes
{
    public static SceneNode Node(string id, string type = "circle", params SceneNode[] children)
        => new(id, type, new Transform(), null, [], children, [], []);

    public static SceneDefinition Ino() => new(
        [new("sphere", "sphere", new Transform(), null, new() { ["icon"] = "ino", ["blackboard"] = """{"mood":"idle"}""" }, [], [], ["tap"])
        {
            Tree = new BehaviorTree(TreeNode.Selector(
                TreeNode.Sequence(false, TreeNode.Condition("mood", "thinking"), TreeNode.Pulse(0.2, 1)),
                TreeNode.Repeat(TreeNode.Wait(1)))),
        }],
        new SceneCamera());
}
