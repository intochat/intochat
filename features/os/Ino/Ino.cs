using DigitalBrain.AI.Agents;
using DigitalBrain.Client;
using DigitalBrain.Flame.Scene;
using DigitalBrain.Flame.Stage;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Text.Signals;

// Shared by every Ino synapse: keys, the two scenes (always rebuilt whole, never patched) and the presence events.
public static class Ino
{
    public static string Key(DigitalBrainConnection brain) => brain.Setting("App")!;
    public static string Scope(DigitalBrainConnection brain) => brain.Setting("Brain") ?? Key(brain)[..Key(brain).IndexOf("/apps/", StringComparison.Ordinal)];
    public static string HomeKey(DigitalBrainConnection brain) => Key(brain) + "/scenes/home";
    public static string WelcomeKey(DigitalBrainConnection brain) => Key(brain) + "/scenes/welcome";
    public static string AssistantKey(DigitalBrainConnection brain) => Scope(brain) + "/apps/assistant";

    public static IScene Home(DigitalBrainConnection brain) => brain.Get<IScene>(HomeKey(brain));
    public static IStage Stage(DigitalBrainConnection brain) => brain.Get<IStage>(brain.Setting("Stage") ?? Scope(brain) + "/stage/owner");

    // The assistant's selected conversation, following its pointer text as the assistant's own projections do.
    public static Sources Conversation(DigitalBrainConnection brain)
        => Sources.Named<IText, TextChanged>(brain.Get<IText>(AssistantKey(brain) + "/conversation"), async (pointer, _) =>
        {
            var selected = (await pointer.Read()).Markdown;
            return [brain.Get<IAgent>(AssistantKey(brain) + "/conversations/" + (selected.Length == 0 ? "main" : selected))];
        });

    static SceneNode Sphere(double scale) => new("ino", "ino_sphere", new Transform(Scale: scale), null,
        new() { ["mood"] = "idle", ["radius"] = "120" }, [], [], ["tap"]);

    static StateMachine Presence() => new("presence", "idle",
        [
            new("idle", [new("ino", null, new() { ["mood"] = "idle" }, null, null)]),
            new("listening", [new("ino", null, new() { ["mood"] = "listening" }, null, null)]),
            new("thinking", [new("ino", null, new() { ["mood"] = "thinking" }, null, null)]),
            new("acting", [new("ino", null, new() { ["mood"] = "acting" }, null, null)]),
            new("speaking", [new("ino", null, new() { ["mood"] = "speaking" }, null, null)]),
            new("error", [new("ino", null, new() { ["mood"] = "error" }, null, null)]),
        ],
        [
            new("idle", "listen", "listening"), new("listening", "think", "thinking"), new("idle", "think", "thinking"),
            new("thinking", "act", "acting"), new("acting", "act", "acting"), new("acting", "speak", "speaking"), new("thinking", "speak", "speaking"),
            new("speaking", "rest", "idle"), new("thinking", "rest", "idle"), new("acting", "rest", "idle"), new("listening", "rest", "idle"),
            new("thinking", "fail", "error"), new("acting", "fail", "error"), new("error", "rest", "idle"),
            new("listening", "fail", "error"), new("listening", "speak", "speaking"), new("error", "think", "thinking"), new("error", "listen", "listening"),
        ]);

    public static SceneDefinition WelcomeScene() => new([Sphere(1.4)], [Presence()], new SceneCamera(), "#070A0F");

    public static SceneDefinition HomeScene(string itemsJson) => new(
        [
            Sphere(1.0),
            new("ring", "activity_ring", new Transform(), null, new() { ["items"] = itemsJson, ["radius"] = "220" }, [], [], []),
        ],
        [Presence()], new SceneCamera(), "#0B0F14");

    public static async Task Compose(DigitalBrainConnection brain, string itemsJson = "[]")
    {
        var welcome = brain.Get<IScene>(WelcomeKey(brain));
        await welcome.Set(WelcomeScene(), (await welcome.Read()).Revision);
        await ComposeHome(brain, itemsJson);
    }

    public static async Task ComposeHome(DigitalBrainConnection brain, string itemsJson)
    {
        var home = Home(brain);
        await home.Set(HomeScene(itemsJson), (await home.Read()).Revision);
    }

    // Rests only while the presence is still in the state the caller left it in, so a newer event is not undone.
    public static async Task RestFrom(DigitalBrainConnection brain, string state)
    {
        try { if ((await Home(brain).Read()).Current.GetValueOrDefault("presence") != state) { return; } }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { return; }
        await Fire(brain, "rest");
    }

    // The assistant's own rule for a message naming an app (Names in features/os/Assistant/routed.cs), copied:
    // Ino docks only what the assistant would open.
    public static bool Names(string message, string name, string title)
    {
        static string Plain(string text) => text.Replace('-', ' ').Replace('_', ' ').Trim();
        return message.Contains(Plain(name), StringComparison.OrdinalIgnoreCase) || message.Contains(name, StringComparison.OrdinalIgnoreCase)
            || (Plain(title).Length > 0 && message.Contains(Plain(title), StringComparison.OrdinalIgnoreCase));
    }

    // Fire never throws into a synapse loop: a presence event that finds no transition is a no-op by contract,
    // and a scene not yet composed is simply skipped.
    public static async Task Fire(DigitalBrainConnection brain, string eventName)
    {
        try { await Home(brain).Fire(eventName); }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
    }
}
