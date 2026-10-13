using DigitalBrain.Apps;
using System.Text.Json;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Text;

// Shared by every BrainView synapse: keys, the panel (always rebuilt whole), and the
// recent-runs log runs.cs keeps in {app}/runs/recent for the panel.
public static class BrainView
{
    public sealed record Run(string Behavior, string File, string Outcome, long DurationMicroseconds, string Trace, string Mark);

    public const string Title = "Behavior runs (kernel activity projection pending)";

    public static string Key(DigitalBrainConnection brain) => brain.Setting("App")!;
    public static string Scope(DigitalBrainConnection brain) => brain.Setting("Brain") ?? Key(brain)[..Key(brain).IndexOf("/apps/", StringComparison.Ordinal)];

    public static ICard Selected(DigitalBrainConnection brain) => brain.Get<ICard>(Key(brain) + "/cards/selected");
    public static IText Runs(DigitalBrainConnection brain) => brain.Get<IText>(Key(brain) + "/runs");
    public static IText Recent(DigitalBrainConnection brain) => brain.Get<IText>(Key(brain) + "/runs/recent");

    // Keeps the current selection: the card's title is the selected neuron, and "open" rebuilds around it.
    public static async Task Compose(DigitalBrainConnection brain)
    {
        var app = Key(brain);
        var card = (await Selected(brain).Read()).Title;
        var selected = card == NoSelection ? "" : card;
        if (selected.Length == 0) { await Selected(brain).Set(NoSelection, "Behavior run details appear here."); }
        if ((await Runs(brain).Read()).Markdown.Length == 0) { await Runs(brain).Set("No runs seen yet."); }
        var column = brain.Get<ILayout>(app + "/panel");
        await column.Set(new("column", [new(UIVocabulary.CardType, app + "/cards/selected"), new(UIVocabulary.TextType, app + "/runs")]), (await column.Read()).Revision);
        var main = brain.Get<ILayout>(app + "/main");
        await main.Set(new("column", [new("layout", app + "/panel")]), (await main.Read()).Revision);
        await brain.Get<IApp>(app).Present(long.Parse(brain.Setting("ProgramRevision")!, System.Globalization.CultureInfo.InvariantCulture),
            new(new Sphere("icon.svg", []), Main: new("layout", app + "/main")));
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
