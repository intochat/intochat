using IntoChat.OS.Apps;
using System.Text.Json;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.Views;
using IntoChat.OS.Microsoft.CSharp.Runtime;

// Shared by every BrainView synapse: keys, the panel (always rebuilt whole), and the
// runtime-owned recent history. The old JSON log is read once for compatibility.
public static class BrainView
{
    public sealed record Run(string Behavior, string File, string Outcome, long DurationMicroseconds, string Trace, string Mark);

    public static string Key(IntoChatOSConnection brain) => brain.Setting("App")!;
    public static string Scope(IntoChatOSConnection brain) => brain.Setting("Brain") ?? Key(brain)[..Key(brain).IndexOf("/apps/", StringComparison.Ordinal)];

    public static IText Recent(IntoChatOSConnection brain) => brain.Get<IText>(Key(brain) + "/runs/recent");
    private static readonly LegacyHistory legacy = new();
    private static ViewPublisher? publisher;

    // Opening advertises the root; the runs reaction alone publishes its immutable tree.
    public static Task Compose(IntoChatOSConnection brain)
    {
        var app = Key(brain);
        return brain.Get<IApp>(app).Present(long.Parse(brain.Setting("ProgramRevision")!, System.Globalization.CultureInfo.InvariantCulture),
            new(new Sphere("icon.svg", []), Main: new(UIVocabulary.ViewType, app + "/view"),
                Views: [new("full", new(UIVocabulary.ViewType, app + "/view"))]));
    }

    public static async Task<Run[]> ReadRecent(IntoChatOSConnection brain)
    {
        var snapshot = await brain.Get<IBrainRuns>(BrainRunsKeys.For(Scope(brain))).Read(Key(brain));
        return Merge(await ReadLegacy(brain), snapshot.Recent, Key(brain));
    }

    public sealed class LegacyHistory
    {
        private Run[]? cached;

        public async Task<Run[]> Read(Func<Task<string>> read) => cached ??= await Load(read);

        private static async Task<Run[]> Load(Func<Task<string>> read)
        {
            var json = await read();
            return json.Length == 0 ? [] : JsonSerializer.Deserialize<Run[]>(json) ?? [];
        }
    }

    private static async Task<Run[]> ReadLegacy(IntoChatOSConnection brain)
    {
        try
        {
            return await legacy.Read(async () => (await Recent(brain).Read()).Markdown);
        }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
        {
            Console.Error.WriteLine("Legacy recent runs could not be read: " + error.Message);
            return [];
        }
    }

    public static Run[] Merge(IEnumerable<Run> legacy, IEnumerable<BrainRunEntry> recent, string owner)
        => [.. legacy.Where(run => run.Outcome != "Skipped" && run.Behavior != owner && !run.Behavior.StartsWith(owner + "/", StringComparison.Ordinal))
            .Concat(recent.Where(run => run.Owner != owner && run.Outcome != IntoChat.OS.Microsoft.CSharp.Runtime.Signals.RunOutcome.Skipped)
                .Select(run => new Run(run.Behavior, run.Behavior[(run.Behavior.LastIndexOf('/') + 1)..],
                run.Outcome.ToString(), run.DurationMicroseconds, run.TraceId ?? "-", run.Mark)))
            .GroupBy(run => run.Mark, StringComparer.Ordinal).Select(group => group.Last()).TakeLast(200)];

    // The panel's run list: the last 10 brain-wide runs, newest first.
    public static UiNode Render(IEnumerable<Run> recent)
    {
        var lines = recent.Reverse().Take(10)
            .Select(run => $"- {run.Behavior} · {run.Outcome} · {run.DurationMicroseconds / 1000.0:0.#} ms · {run.Trace}").ToArray();
        var markdown = lines.Length == 0 ? "No runs seen yet." : string.Join("\n", lines);
        return Ui.Column("activity",
        [
            Ui.Card("heading", "Recent behavior runs", "The latest runs across this brain, newest first."),
            Ui.Text("runs", markdown),
        ]);
    }

    public static Task PublishRecent(IntoChatOSConnection brain, IEnumerable<Run> recent)
    {
        publisher ??= new(brain.Get<IView>(Key(brain) + "/view"),
            long.Parse(brain.Setting("ProgramRevision")!, System.Globalization.CultureInfo.InvariantCulture));
        return publisher.Publish(Render(recent));
    }
}
