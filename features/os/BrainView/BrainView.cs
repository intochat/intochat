using DigitalBrain.Apps;
using System.Text.Json;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Microsoft.CSharp.Runtime;

// Shared by every BrainView synapse: keys, the panel (always rebuilt whole), and the
// runtime-owned recent history. The old JSON log is read once for compatibility.
public static class BrainView
{
    public sealed record Run(string Behavior, string File, string Outcome, long DurationMicroseconds, string Trace, string Mark);

    public static string Key(DigitalBrainConnection brain) => brain.Setting("App")!;
    public static string Scope(DigitalBrainConnection brain) => brain.Setting("Brain") ?? Key(brain)[..Key(brain).IndexOf("/apps/", StringComparison.Ordinal)];

    public static IText Runs(DigitalBrainConnection brain) => brain.Get<IText>(Key(brain) + "/runs");
    public static IText Recent(DigitalBrainConnection brain) => brain.Get<IText>(Key(brain) + "/runs/recent");
    private static Task<Run[]>? legacy;
    private static string? shown;

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
            new(new Sphere("icon.svg", []), Main: new("layout", app + "/main"), Views: [new("full", new("layout", app + "/main"))]));
    }

    public static async Task<Run[]> ReadRecent(DigitalBrainConnection brain)
    {
        legacy ??= ReadLegacy(brain);
        var snapshot = await brain.Get<IBrainRuns>(BrainRunsKeys.For(Scope(brain))).Read(Key(brain));
        return Merge(await legacy, snapshot.Recent, Key(brain));
    }

    private static async Task<Run[]> ReadLegacy(DigitalBrainConnection brain)
    {
        try
        {
            var json = (await Recent(brain).Read()).Markdown;
            return json.Length == 0 ? [] : JsonSerializer.Deserialize<Run[]>(json) ?? [];
        }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
        {
            Console.Error.WriteLine("Legacy recent runs could not be read: " + error.Message);
            return [];
        }
    }

    public static Run[] Merge(IEnumerable<Run> legacy, IEnumerable<BrainRunEntry> recent, string owner)
        => [.. legacy.Where(run => run.Outcome != "Skipped" && run.Behavior != owner && !run.Behavior.StartsWith(owner + "/", StringComparison.Ordinal))
            .Concat(recent.Where(run => run.Owner != owner && run.Outcome != DigitalBrain.Microsoft.CSharp.Runtime.Signals.RunOutcome.Skipped)
                .Select(run => new Run(run.Behavior, run.Behavior[(run.Behavior.LastIndexOf('/') + 1)..],
                run.Outcome.ToString(), run.DurationMicroseconds, run.TraceId ?? "-", run.Mark)))
            .GroupBy(run => run.Mark, StringComparer.Ordinal).Select(group => group.Last()).TakeLast(200)];

    // The panel's run list: the last 10 brain-wide runs, newest first.
    public static async Task ShowRuns(DigitalBrainConnection brain, IEnumerable<Run> recent)
    {
        var lines = recent.Reverse().Take(10)
            .Select(run => $"- {run.Behavior} · {run.Outcome} · {run.DurationMicroseconds / 1000.0:0.#} ms · {run.Trace}").ToArray();
        var markdown = lines.Length == 0 ? "No runs seen yet." : string.Join("\n", lines);
        if (shown == markdown) { return; }
        await Runs(brain).Set(markdown);
        shown = markdown;
    }
}
