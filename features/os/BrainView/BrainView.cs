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

    public static string Key(DigitalBrainConnection brain) => brain.Setting("App")!;
    public static string Scope(DigitalBrainConnection brain) => brain.Setting("Brain") ?? Key(brain)[..Key(brain).IndexOf("/apps/", StringComparison.Ordinal)];

    public static IText Runs(DigitalBrainConnection brain) => brain.Get<IText>(Key(brain) + "/runs");
    public static IText Recent(DigitalBrainConnection brain) => brain.Get<IText>(Key(brain) + "/runs/recent");

    // Rebuild from durable history so opening or restarting the app restores the brain-wide activity.
    public static async Task Compose(DigitalBrainConnection brain)
    {
        var app = Key(brain);
        await brain.Get<ICard>(app + "/cards/activity").Set("Recent behavior runs", "The latest runs across this brain, newest first.");
        await ShowRuns(brain, await ReadRecent(brain));
        var column = brain.Get<ILayout>(app + "/panel");
        await column.Set(new("column", [new(UIVocabulary.CardType, app + "/cards/activity"), new(UIVocabulary.TextType, app + "/runs")]), (await column.Read()).Revision);
        var main = brain.Get<ILayout>(app + "/main");
        await main.Set(new("column", [new("layout", app + "/panel")]), (await main.Read()).Revision);
        await brain.Get<IApp>(app).Present(long.Parse(brain.Setting("ProgramRevision")!, System.Globalization.CultureInfo.InvariantCulture),
            new(new Sphere("icon.svg", []), Main: new("layout", app + "/main")));
    }

    public static async Task<Run[]> ReadRecent(DigitalBrainConnection brain)
    {
        var json = (await Recent(brain).Read()).Markdown;
        return json.Length == 0 ? [] : JsonSerializer.Deserialize<Run[]>(json) ?? [];
    }

    // The panel's run list: the last 10 brain-wide runs, newest first.
    public static Task ShowRuns(DigitalBrainConnection brain, IEnumerable<Run> recent)
    {
        var lines = recent.Reverse().Take(10)
            .Select(run => $"- {run.Behavior} · {run.Outcome} · {run.DurationMicroseconds / 1000.0:0.#} ms · {run.Trace}").ToArray();
        return Runs(brain).Set(lines.Length == 0 ? "No runs seen yet." : string.Join("\n", lines));
    }
}
