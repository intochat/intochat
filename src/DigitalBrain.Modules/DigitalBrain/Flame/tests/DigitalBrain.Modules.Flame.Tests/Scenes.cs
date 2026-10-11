using DigitalBrain.Flame.Scene;

namespace DigitalBrain.Modules.Flame.Tests;

internal static class Scenes
{
    public static SceneNode Node(string id, string type = "circle", params SceneNode[] children)
        => new(id, type, new Transform(), null, [], children, [], []);

    public static StateMachine Machine(string id, string initial, string[] states, params Transition[] transitions)
        => new(id, initial, [.. states.Select(s => new StateDef(s, []))], transitions);

    public static SceneDefinition Ino() => new(
        [Node("sphere", "ino_sphere")],
        [Machine("presence", "idle", ["idle", "thinking", "speaking"],
            new Transition("idle", "think", "thinking"), new Transition("thinking", "speak", "speaking"), new Transition("speaking", "rest", "idle"), new Transition("thinking", "rest", "idle"))],
        new SceneCamera());
}
