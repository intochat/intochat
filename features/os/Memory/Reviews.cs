using System.Globalization;
using System.Text.Json;
using IntoChat.OS;
using IntoChat.OS.AI.Agents.Context;
using IntoChat.OS.AI.Agents.Facts;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Postgres.Tables;

// Shared by "review", "review-accept", "review-reject" and "consolidate": what a user's decision on a review row
// means, how it is applied, and the count of rows still waiting.
internal static class Reviews
{
    public enum Operation { Accept, Reject }

    public enum Outcome { Accept, Reject, AlreadyDone, Unknown }

    public const string CountEntry = "review-count";

    // A pending row takes the decision; a row already accepted or rejected is left as it is; no row is Unknown.
    public static Outcome Decide(string? status, Operation operation)
        => status switch
        {
            "pending" => operation == Operation.Accept ? Outcome.Accept : Outcome.Reject,
            "accepted" or "rejected" => Outcome.AlreadyDone,
            _ => Outcome.Unknown,
        };

    public static string Render(int count) => count <= 0 ? "" : count + (count == 1 ? " memory awaits review" : " memories await review");

    public static async Task<int> Count(ITable table)
    {
        var count = 0;
        for (var offset = 0; ; offset += 200)
        {
            var page = await table.Page(offset, 200);
            count += page.Count(cells => cells.Any(cell => cell.Column == "status" && cell.Json.Trim('"') == "pending"));
            if (page.Length < 200) { return count; }
        }
    }

    // Rows as stored (JSON literals per column).
    public static async Task<List<Dictionary<string, string>>> Rows(ITable reviews)
    {
        var rows = new List<Dictionary<string, string>>();
        for (var offset = 0; ; offset += 200)
        {
            var page = await reviews.Page(offset, 200);
            rows.AddRange(page.Select(cells => cells.ToDictionary(cell => cell.Column, cell => cell.Json, StringComparer.Ordinal)));
            if (page.Length < 200) { return rows; }
        }
    }

    // A stored row read as plain text per column.
    public static Dictionary<string, string> View(Dictionary<string, string> raw)
        => raw.ToDictionary(cell => cell.Key, cell => Text(cell.Value), StringComparer.Ordinal);

    private static string Text(string literal)
    {
        using var document = JsonDocument.Parse(literal);
        return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() ?? "" : document.RootElement.GetRawText();
    }

    private static readonly SemaphoreSlim Deciding = new(1, 1);

    // One decision: Accept asserts the fact then marks the row; Reject marks it. Failures are reported on {app}/status.
    public static async Task Apply(IntoChatOSConnection brain, Operation kind, string id)
    {
        var appKey = brain.Setting("App")!;
        var reviews = brain.Get<ITable>(Consolidation.ReviewTableKey(appKey));
        await Deciding.WaitAsync(brain.Stopping);
        try
        {
            var raw = (await Rows(reviews)).FirstOrDefault(cells => View(cells).GetValueOrDefault("id") == id);
            var row = raw is null ? null : View(raw);
            switch (Decide(row?.GetValueOrDefault("status"), kind))
            {
                case Outcome.Accept:
                    await Assert(brain, appKey, id, row!);
                    await Mark(reviews, raw!, "accepted");
                    break;
                case Outcome.Reject:
                    await Mark(reviews, raw!, "rejected");
                    break;
                case Outcome.Unknown:
                    await Report(brain, appKey, "Memory has no review item " + id);
                    break;
            }
            await Refresh(brain);
        }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
        { await Report(brain, appKey, "Memory could not " + kind.ToString().ToLowerInvariant() + " the review item: " + error.Message); }
        finally { Deciding.Release(); }
    }

    private static async Task Assert(IntoChatOSConnection brain, string appKey, string id, Dictionary<string, string> row)
    {
        var facts = brain.Get<IFacts>(IFacts.Key(Memory.Scope(brain)));
        var kind = row.GetValueOrDefault("kind", "") switch { "profile" => FactKind.Profile, "insight" => FactKind.Insight, _ => FactKind.Fact };
        var text = row.GetValueOrDefault("text", "");
        if (text.Length > IFacts.TextLimit) { text = text[..IFacts.TextLimit]; }
        var subject = row.GetValueOrDefault("subject", "");
        if (subject.Length > IFacts.SubjectLimit) { subject = subject[..IFacts.SubjectLimit]; }
        var confidence = double.TryParse(row.GetValueOrDefault("confidence"), CultureInfo.InvariantCulture, out var c) ? c : 1.0;
        var importance = int.TryParse(row.GetValueOrDefault("importance"), CultureInfo.InvariantCulture, out var i) ? i : 5;
        await facts.Assert(new FactAssertion(kind, text, subject, confidence, importance, DateTimeOffset.UtcNow, appKey, 0, "review", [], id));
    }

    private static async Task Mark(ITable reviews, Dictionary<string, string> raw, string value)
    {
        // Every column is written back as it was read, so an update never depends on whether Upsert merges or replaces.
        var values = raw.Where(cell => cell.Key != "id" && cell.Key != "status").Select(cell => new TableValue(cell.Key, cell.Value))
            .Append(new TableValue("status", JsonSerializer.Serialize(value))).ToArray();
        await reviews.Upsert([new("id", raw["id"])], values);
    }

    // The context's "review-count" entry follows the pending review rows.
    public static async Task Refresh(IntoChatOSConnection brain)
    {
        var appKey = brain.Setting("App")!;
        var context = brain.Get<IAgentContext>(Intents.ContextKey(appKey));
        var text = Render(await Count(brain.Get<ITable>(Consolidation.ReviewTableKey(appKey))));
        var current = (await context.Read()).Entries.FirstOrDefault(entry => entry.Name == CountEntry);
        if (text.Length == 0) { if (current is not null) { await context.Remove(CountEntry); } }
        else if (current?.Text != text) { await context.Set(new(CountEntry, text, [typeof(ITable).FullName!])); }
    }

    private static async Task Report(IntoChatOSConnection brain, string appKey, string text)
    {
        try { await brain.Get<IText>(appKey + "/status").Set(text.Length <= 200 ? text : text[..200]); }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
    }
}
