using System.Text;
using System.Text.Json;
using IntoChat.OS.AI.Agents.Intents;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Text;

// A message waiting for Memory to intend it: what was asked, the hint Jev gave, the conversation and when it
// was handed to Memory. Started is set once the answer's turn has been started, so a late Rephrased signal
// and the slow-Memory scan never answer the same message twice. One JSON text per request at
// {data}/pending/{requestId}; each writing synapse keeps the ids it wrote in its own index text
// ({data}/pending/index-{synapse}), so every index has a single writer. Attempts counts the answers that could
// not start, the last of them at ReleasedAt.
public sealed record Pending(string Message, string Hint, string Conversation, DateTimeOffset StartedAt, bool Started,
    int Attempts = 0, DateTimeOffset? ReleasedAt = null)
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

    public static string EntryKey(IntoChatOSConnection brain, Guid requestId) => Ino.DataKey(brain) + "/pending/" + requestId.ToString("N");
    public static string IndexKey(IntoChatOSConnection brain, string synapse) => Ino.DataKey(brain) + "/pending/index-" + synapse;

    public const int MaxAttempts = 3;

    // The wait after the n-th answer that could not start: 2 s, 4 s, 8 s.
    public static TimeSpan Backoff(int attempts) => TimeSpan.FromSeconds(2 << Math.Clamp(attempts - 1, 0, 2));

    // The requests handed to Memory longer ago than the gate whose answer has not started and was never released.
    public static Guid[] Older(IReadOnlyDictionary<Guid, Pending> entries, DateTimeOffset now, TimeSpan gate)
        => [.. entries.Where(entry => Decide(entry.Value, now, gate) == PendingStep.Answer && entry.Value.Attempts == 0).Select(entry => entry.Key)];

    // What the scan does with an entry: nothing while it is started or backing off, watch Memory's answer while
    // the gate is open, and answer (from Memory's answer text, with or without a brief) once the gate has passed
    // or a released request's backoff has.
    public static PendingStep Decide(Pending pending, DateTimeOffset now, TimeSpan gate)
        => pending.Started ? PendingStep.Wait
            : pending.Attempts > 0 ? (now - (pending.ReleasedAt ?? pending.StartedAt) >= Backoff(pending.Attempts) ? PendingStep.Answer : PendingStep.Wait)
            : now - pending.StartedAt >= gate ? PendingStep.Answer : PendingStep.Watch;

    // The entry after an answer that could not start: given back with the attempt recorded, or, at the cap, kept
    // claimed (so it is never tried again) for the one-hour prune. Retry is false at the cap.
    public static (Pending Entry, bool Retry) Released(Pending pending, DateTimeOffset now)
    {
        var attempts = pending.Attempts + 1;
        return attempts >= MaxAttempts
            ? (pending with { Started = true, Attempts = attempts, ReleasedAt = now }, false)
            : (pending with { Started = false, Attempts = attempts, ReleasedAt = now }, true);
    }

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

    // Claims a request for answering: true when it was pending and not started, and now is. Claiming before the
    // turn starts means a crash between the two loses that answer rather than giving it twice.
    public static async Task<bool> Claim(IntoChatOSConnection brain, Guid requestId)
    {
        if (await Read(brain, requestId) is not { Started: false } pending) { return false; }
        await brain.Get<IText>(EntryKey(brain, requestId)).Set((pending with { Started = true }).Serialize());
        return true;
    }

    // A turn that could not start gives the request back, so the scan tries it again after a backoff; false when
    // the cap is reached and the request is not tried again.
    public static async Task<bool> Release(IntoChatOSConnection brain, Guid requestId)
    {
        if (await Read(brain, requestId) is not { Started: true } pending) { return true; }
        var (entry, retry) = Released(pending, DateTimeOffset.UtcNow);
        await brain.Get<IText>(EntryKey(brain, requestId)).Set(entry.Serialize());
        return retry;
    }

    public static bool KeepsClaim(AnswerOutcome outcome) => outcome != AnswerOutcome.Failed;

    // Memory's answer under the request id: "ok <intent id>" once the intent is rephrased (and confirmed when it
    // was sure), "failed: ..." otherwise.
    public static MemoryAnswer Classify(string text)
        => text == "ok" || text.StartsWith("ok ", StringComparison.Ordinal) ? MemoryAnswer.Ok
            : text.StartsWith("failed:", StringComparison.Ordinal) ? MemoryAnswer.Failed : MemoryAnswer.None;

    // The intent id of an "ok <intent id>" answer; null for a bare "ok" or any other text.
    public static Guid? IntentId(string text)
        => text.StartsWith("ok ", StringComparison.Ordinal) && Guid.TryParse(text.AsSpan(3).Trim(), out var id) ? id : null;

    // The intent an "ok" answer names: by its id, or by the request id when the answer carries none. A continued
    // intent keeps the request id it was first heard under, so only the id finds it.
    public static Intent? Find(Intent[] intents, string answer, Guid requestId)
        => IntentId(answer) is { } id
            ? intents.FirstOrDefault(intent => intent.Id == id)
            : intents.LastOrDefault(intent => intent.RequestId == requestId.ToString("N"));

    public static Guid[] Expired(IReadOnlyDictionary<Guid, Pending> entries, DateTimeOffset now)
        => [.. entries.Where(entry => now - entry.Value.StartedAt > Kept).Select(entry => entry.Key)];

    // Every pending entry the writers' indexes name; entries older than an hour are deleted on the way.
    public static async Task<Dictionary<Guid, Pending>> Entries(IntoChatOSConnection brain)
    {
        var entries = new Dictionary<Guid, Pending>();
        foreach (var synapse in Writers)
        {
            foreach (var id in Ids((await brain.Get<IText>(IndexKey(brain, synapse)).Read()).Markdown))
            { if (await Read(brain, id) is { } pending) { entries[id] = pending; } }
        }
        foreach (var id in Expired(entries, DateTimeOffset.UtcNow))
        {
            await brain.Get<IText>(EntryKey(brain, id)).Set("");
            entries.Remove(id);
        }
        return entries;
    }

    // Deletes the entries older than an hour; the watch path calls it so cleanup does not wait for the scan.
    public static async Task Prune(IntoChatOSConnection brain) => await Entries(brain);

    public static async Task<Guid[]> Older(IntoChatOSConnection brain, TimeSpan gate)
        => Older(await Entries(brain), DateTimeOffset.UtcNow, gate);

    private static IEnumerable<Guid> Ids(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => Guid.TryParse(line, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty);
}

// NoModel: nothing can answer (no usable model); the first-run card shows, no turn starts, the claim is kept.
public enum AnswerOutcome { Started, KeptInDraft, Failed, NoModel }

public enum MemoryAnswer { None, Ok, Failed }

public enum PendingStep { Wait, Watch, Answer }

public static partial class Ino
{
    // Whether Memory's intent is acted on rather than asked about: Confirmed, or already Dispatched for this very
    // request (Dispatched cannot be reversed, so a turn that failed to start after it is acted on again).
    public static bool ConfirmedFor(Intent? intent, Guid requestId)
        => intent is { State: IntentState.Confirmed }
            || intent is { State: IntentState.Dispatched } && intent.RequestId == requestId.ToString("N");

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
