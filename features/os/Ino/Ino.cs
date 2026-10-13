using DigitalBrain.AI.Agents;
using DigitalBrain.Client;
using DigitalBrain.Flame.Scene;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Text.Signals;
using System.Text.Json;

// Shared by every Ino synapse: keys, the two scenes (always rebuilt whole, never patched) and the presence events.
public static class Ino
{
    public static string Key(DigitalBrainConnection brain) => brain.Setting("App")!;
    public static string Scope(DigitalBrainConnection brain) => brain.Setting("Brain") ?? Key(brain)[..Key(brain).IndexOf("/apps/", StringComparison.Ordinal)];
    public static string HomeKey(DigitalBrainConnection brain) => Key(brain) + "/scenes/home";
    public static string WelcomeKey(DigitalBrainConnection brain) => Key(brain) + "/scenes/welcome";
    public static string AssistantKey(DigitalBrainConnection brain) => Scope(brain) + "/apps/assistant";

    public static IScene Home(DigitalBrainConnection brain) => brain.Get<IScene>(HomeKey(brain));

    // The assistant's selected conversation, following its pointer text as the assistant's own projections do.
    public static Sources Conversation(DigitalBrainConnection brain)
        => Sources.Named<IText, TextChanged>(brain.Get<IText>(AssistantKey(brain) + "/conversation"), async (pointer, _) =>
        {
            var selected = (await pointer.Read()).Markdown;
            return [brain.Get<IAgent>(AssistantKey(brain) + "/conversations/" + (selected.Length == 0 ? "main" : selected))];
        });

    static SceneNode Sphere(double scale) => new("ino", "ino_sphere", new Transform(Scale: scale), null,
        new() { ["mood"] = "idle", ["radius"] = "120" }, [], [], ["tap"]);

    public static SceneDefinition WelcomeScene() => new([Sphere(1.4)], new SceneCamera(), "#070A0F");

    public static SceneDefinition HomeScene(string itemsJson) => new(
        [
            Sphere(1.0),
            new("ring", "activity_ring", new Transform(), null, new() { ["items"] = itemsJson, ["radius"] = "220" }, [], [], []),
        ],
        new SceneCamera(), "#0B0F14");

    public static async Task Compose(DigitalBrainConnection brain)
    {
        var welcome = brain.Get<IScene>(WelcomeKey(brain));
        var initial = await welcome.Read();
        if (initial.Revision == 0) { await welcome.Set(WelcomeScene(), initial.Revision); }
        var home = await Home(brain).Read();
        await ComposeHome(brain, JsonSerializer.Serialize(RecentApps(home.Definition)));
    }

    public static JsonElement[] RecentApps(SceneDefinition definition)
    {
        var items = definition.Nodes.FirstOrDefault(node => node.Id == "ring")?.Parameters.GetValueOrDefault("items");
        return string.IsNullOrWhiteSpace(items) ? [] : (JsonSerializer.Deserialize<JsonElement[]>(items) ?? [])
            .Where(item => item.TryGetProperty("kind", out var kind) && kind.GetString() == "app")
            .TakeLast(6).ToArray();
    }

    public static async Task ComposeHome(DigitalBrainConnection brain, string itemsJson)
    {
        var home = Home(brain);
        await home.Set(HomeScene(itemsJson), (await home.Read()).Revision);
    }

    // Presence is rewritten as a behavior tree in a later step; until then the presence synapses keep their shape and do nothing.
    public static Task RestFrom(DigitalBrainConnection brain, string state) => Task.CompletedTask;

    public static Task Fire(DigitalBrainConnection brain, string eventName) => Task.CompletedTask;
}
