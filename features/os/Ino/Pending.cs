using System.Text;
using System.Text.Json;
using IntoChat.OS.AI.Agents.Intents;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Text;

// A message waiting for Memory to intend it: what was asked, the hint Jev gave, the conversation and when it
// was handed to Memory. Started is set once the answer's turn has been started, so a late Rephrased signal
// and the slow-Memory scan never answer the same message twice. One JSON text per request at
// {data}/pending/{requestId}; each writing synapse keeps the ids it wrote in its own index text
// ({data}/pending/index-{synapse}), so every index has a single writer.
public sealed record Pending(string Message, string Hint, string Conversation, DateTimeOffset StartedAt, bool Started)
{
    public string Serialize() => JsonSerializer.Serialize(this);

    public static Pending? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) { return null; }
        try { return JsonSerializer.Deserialize<Pending>(json); }
        catch (JsonException) { return null; }
    }
}

public static class PendingStore
{
    public static readonly TimeSpan Kept = TimeSpan.FromHours(1);
    public static readonly string[] Writers = ["routed", "send"];

    // Ino.DataKey, repeated so this file compiles on its own into the unit tests.
    private static string Data(IntoChatOSConnection brain) => brain.Setting("ChatDataApp") is { Length: > 0 } data ? data : brain.Setting("App")!;

    public static string EntryKey(IntoChatOSConnection brain, Guid requestId) => Data(brain) + "/pending/" + requestId.ToString("N");
    public static string IndexKey(IntoChatOSConnection brain, string synapse) => Data(brain) + "/pending/index-" + synapse;

    // The requests handed to Memory longer ago than the gate whose answer has not started.
    public static Guid[] Older(IReadOnlyDictionary<Guid, Pending> entries, DateTimeOffset now, TimeSpan gate)
        => [.. entries.Where(entry => !entry.Value.Started && now - entry.Value.StartedAt >= gate).Select(entry => entry.Key)];

    // Writes the entry and adds it to the synapse's index; entries older than an hour are deleted on the way.
    public static async Task Write(IntoChatOSConnection brain, string synapse, Guid requestId, Pending pending)
    {
        await brain.Get<IText>(EntryKey(brain, requestId)).Set(pending.Serialize());
        var index = brain.Get<IText>(IndexKey(brain, synapse));
        var kept = new List<Guid>();
        foreach (var id in Ids((await index.Read()).Markdown).Where(id => id != requestId))
        {
            var entry = await Read(brain, id);
            if (entry is null) { continue; }
            if (pending.StartedAt - entry.StartedAt > Kept) { await brain.Get<IText>(EntryKey(brain, id)).Set(""); continue; }
            kept.Add(id);
        }
        kept.Add(requestId);
        await index.Set(string.Join('\n', kept.Select(id => id.ToString("N"))));
    }

    public static async Task<Pending?> Read(IntoChatOSConnection brain, Guid requestId)
        => Pending.Parse((await brain.Get<IText>(EntryKey(brain, requestId)).Read()).Markdown);

    public static async Task MarkStarted(IntoChatOSConnection brain, Guid requestId)
    {
        if (await Read(brain, requestId) is { Started: false } pending)
        { await brain.Get<IText>(EntryKey(brain, requestId)).Set((pending with { Started = true }).Serialize()); }
    }

    public static async Task<Guid[]> Older(IntoChatOSConnection brain, TimeSpan gate)
    {
        var entries = new Dictionary<Guid, Pending>();
        foreach (var synapse in Writers)
        {
            foreach (var id in Ids((await brain.Get<IText>(IndexKey(brain, synapse)).Read()).Markdown))
            { if (await Read(brain, id) is { } pending) { entries[id] = pending; } }
        }
        var now = DateTimeOffset.UtcNow;
        // Older than an hour is no longer worth answering: forget it instead.
        return [.. Older(entries, now, gate).Where(id => now - entries[id].StartedAt <= Kept)];
    }

    private static IEnumerable<Guid> Ids(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => Guid.TryParse(line, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty);
}

public static partial class Ino
{
    // The brief Memory wrote, as the turn's instruction: a confirmed brief is acted on, an unconfirmed one is asked about.
    public static string BriefInstructions(IntentBrief brief, bool confirmed)
    {
        var text = new StringBuilder("\n\n");
        text.AppendLine(confirmed ? "Act on this brief:" : "Show this brief to the user, ask whether it is right, and do not act on it yet:");
        text.AppendLine("Goal: " + brief.Goal);
        static void List(StringBuilder text, string name, string[] items)
        {
            if (items.Length == 0) { return; }
            text.AppendLine(name + ":");
            foreach (var item in items) { text.AppendLine("- " + item); }
        }
        List(text, "Inputs", brief.Inputs);
        List(text, "Constraints", brief.Constraints);
        List(text, "Acceptance", brief.Acceptance);
        return text.ToString();
    }
}
