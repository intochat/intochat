using System.Text.Json;
using DigitalBrain.Client;
using DigitalBrain.Flame.Scene;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.Text;

// Shared by every BrainView synapse: keys, the brain scene and the panel (both always rebuilt whole), and the
// recent-runs log runs.cs keeps in {app}/runs/recent so the selection synapse can read it.
public static class BrainView
{
    public sealed record Run(string Behavior, string File, string Outcome, long DurationMicroseconds, string Trace, string Mark);

    public const string Title = "Behavior runs (kernel activity projection pending)";

    public static string Key(DigitalBrainConnection brain) => brain.Setting("App")!;
    public static string Scope(DigitalBrainConnection brain) => brain.Setting("Brain") ?? Key(brain)[..Key(brain).IndexOf("/apps/", StringComparison.Ordinal)];
    public static string SceneKey(DigitalBrainConnection brain) => Key(brain) + "/scenes/brain";
    public static string SurfaceKey(DigitalBrainConnection brain) => Key(brain) + "/surface";

    public static IScene Scene(DigitalBrainConnection brain) => brain.Get<IScene>(SceneKey(brain));
    public static ICard Selected(DigitalBrainConnection brain) => brain.Get<ICard>(Key(brain) + "/cards/selected");
    public static IText Runs(DigitalBrainConnection brain) => brain.Get<IText>(Key(brain) + "/runs");
    public static IText Recent(DigitalBrainConnection brain) => brain.Get<IText>(Key(brain) + "/runs/recent");

    public static SceneDefinition BrainScene(string? selected)
    {
        var parameters = new Dictionary<string, string> { ["icon"] = "hub", ["title"] = selected is { Length: > 0 } ? selected : "Brain", ["neuron"] = selected ?? "" };
        return new([new("brain", "sphere", new Transform(), null, parameters, [], [], ["tap"])], new SceneCamera(), "#05080C");
    }

    public static async Task ComposeScene(DigitalBrainConnection brain, string? selected)
    {
        var scene = Scene(brain);
        await scene.Set(BrainScene(selected), (await scene.Read()).Revision);
    }

    // Keeps the current selection: the card's title is the selected neuron, and "open" rebuilds around it.
    public static async Task Compose(DigitalBrainConnection brain)
    {
        var app = Key(brain);
        var card = (await Selected(brain).Read()).Title;
        var selected = card == NoSelection ? "" : card;
        await ComposeScene(brain, selected);
        if (selected.Length == 0) { await Selected(brain).Set(NoSelection, "Tap a point on the sphere."); }
        if ((await Runs(brain).Read()).Markdown.Length == 0) { await Runs(brain).Set("No runs seen yet."); }
        var column = brain.Get<ILayout>(app + "/panel");
        await column.Set(new("column", [new(UIVocabulary.CardType, app + "/cards/selected"), new(UIVocabulary.TextType, app + "/runs")]), (await column.Read()).Revision);
        var surface = brain.Get<ISurface>(SurfaceKey(brain));
        var main = brain.Get<ILayout>(app + "/main");
        await main.Set(new("row", [new("scene", SceneKey(brain)), new("layout", app + "/panel")], Extents: [0, 320]), (await main.Read()).Revision);
        await surface.Set(new("Brain", [new("layout", app + "/main")]), (await surface.Read()).Revision);
    }

    public const string NoSelection = "No neuron selected";

    public static async Task<Run[]> ReadRecent(DigitalBrainConnection brain)
    {
        var json = (await Recent(brain).Read()).Markdown;
        return json.Length == 0 ? [] : JsonSerializer.Deserialize<Run[]>(json) ?? [];
    }

    public static bool Under(string behavior, string selected)
        => behavior == selected || behavior.StartsWith(selected + "/", StringComparison.Ordinal);

    // The panel's run list: the last 10 runs of behaviors under the selected key, newest first.
    public static Task ShowRuns(DigitalBrainConnection brain, string selected, IEnumerable<Run> recent)
    {
        var lines = recent.Where(run => Under(run.Behavior, selected)).Reverse().Take(10)
            .Select(run => $"- {run.File} · {run.Outcome} · {run.DurationMicroseconds / 1000.0:0.#} ms · {run.Trace}").ToArray();
        return Runs(brain).Set(lines.Length == 0 ? "No runs seen yet." : string.Join("\n", lines));
    }
}
