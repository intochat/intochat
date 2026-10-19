using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// Shared by "consolidate" and "review": where the consolidation keeps its state, how the model's verdict is
// read, how an item is classified (the sensitivity policy), when a batch reflects and when an episode may be claimed.
internal static class Consolidation
{
    public enum Disposition { Publish, Review, Discard }

    // One item of the model's {"items":[...]} verdict, as the model wrote it.
    public sealed record Verdict(string Disposition, string[] EpisodeIds, string Kind, string Text, string Subject,
        double Confidence, int Importance, string Reason, string? Contradicts);

    public sealed record Decision(Disposition Disposition, string Reason);

    public const int BatchSize = 20;
    public const int ReflectionThreshold = 30;
    public const int MaxInsights = 3;
    public static readonly TimeSpan ClaimFor = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(60);

    public static string AgentKey(string appKey) => appKey + "/agents/consolidate";

    public static string ReminderKey(string appKey) => appKey + "/consolidation";

    // The review table and the review text neuron are different neurons, so they take different keys.
    public static string ReviewTableKey(string appKey) => appKey + "/review-items";

    public static string ReviewTextKey(string appKey) => appKey + "/review";

    public static string ArchiveKey(string appKey) => appKey + "/facts-archive";

    // Credentials are never stored, not even for review.
    public static readonly Regex Secret = new(
        @"\b(sk|pk|rk)[-_](live|test)[-_][A-Za-z0-9]{8,}|\bsk-[A-Za-z0-9_-]{16,}|\bAKIA[0-9A-Z]{16}\b|\bgh[pousr]_[A-Za-z0-9]{20,}|\bxox[abpr]-[A-Za-z0-9-]{10,}"
        + @"|-----BEGIN [A-Z ]*PRIVATE KEY-----|\b(password|passwd|pwd|passcode|api[ _-]?key|secret|token|pin)\s*(is|[:=])\s*\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Never auto-published; always sent to review.
    public static readonly IReadOnlyDictionary<string, Regex> SensitiveClasses = new Dictionary<string, Regex>
    {
        ["health"] = Words(@"diagnos\w*|illness|disease|diabetes|cancer|depress\w*|anxiety|medication|medicine|prescription|therap\w*|pregnan\w*|allerg\w*|symptoms?|doctor|hospital|surgery|health"),
        ["finances"] = Words(@"salary|income|debt|loans?|mortgage|bank\s+account|credit\s+card|iban|payments?|paid|invoices?|tax(es)?|savings|bankrupt\w*|finances?|financial"),
        ["third party"] = Words(@"(his|her|their)\s+(wife|husband|partner|son|daughter|mother|father|boss|friend|colleague)|(wife|husband|partner|girlfriend|boyfriend|mother|father|sister|brother|colleague|friend|boss)\s+\p{Lu}\w*"),
        ["location"] = Words(@"lives\s+(at|in|on)|home\s+address|address\s+is|street|apartment|commutes?|every\s+(morning|evening|day|monday|tuesday|wednesday|thursday|friday|saturday|sunday)|routine|located\s+at"),
    };

    private static Regex Words(string alternatives)
        => new(@"\b(" + alternatives + @")\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Secret first (discard), then the model's own discard, then sensitivity and the confidence gate (review).
    public static Decision Classify(Verdict item, double reviewGate, IReadOnlyDictionary<string, Regex> sensitiveClasses)
    {
        var text = item.Text + "\n" + item.Subject;
        if (Secret.IsMatch(text)) { return new(Disposition.Discard, "secret"); }
        if (item.Disposition == "discard") { return new(Disposition.Discard, item.Reason.Length > 0 ? item.Reason : "transient"); }
        foreach (var (name, pattern) in sensitiveClasses)
        {
            if (pattern.IsMatch(text)) { return new(Disposition.Review, "sensitive: " + name); }
        }
        if (item.Confidence < reviewGate) { return new(Disposition.Review, "low confidence"); }
        if (item.Disposition != "publish") { return new(Disposition.Review, item.Reason.Length > 0 ? item.Reason : "uncertain"); }
        return new(Disposition.Publish, item.Reason);
    }

    // The batch asks for insights when its episodes (none of them reflected yet) sum to the threshold.
    public static bool Reflect(IEnumerable<int> importances) => importances.Sum() >= ReflectionThreshold;

    // Unconsolidated, and either never claimed or its claim expired.
    public static bool Claimable(DateTimeOffset? consolidatedAt, string? claim, DateTimeOffset? claimUntil, DateTimeOffset now)
        => consolidatedAt is null && (string.IsNullOrEmpty(claim) || claimUntil is null || claimUntil <= now);

    // The first 12 hex characters of the SHA-256 of the prompt text.
    public static string PromptVersion(string prompt)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt ?? ""))).ToLowerInvariant()[..12];

    // Idempotent per batch: the first episode id and the item's ordinal.
    public static string DedupKey(Verdict item, int ordinal) => (item.EpisodeIds.FirstOrDefault() ?? "") + "#" + ordinal;

    // Insights keep their evidence episode ids in the text: episode ids are not fact ids, so Links stay empty.
    public static string WithEvidence(string text, string[] episodeIds)
        => episodeIds.Length == 0 ? text : text + " (evidence: " + string.Join(", ", episodeIds) + ")";

    public static TimeSpan Quiet(string? minutes)
    {
        var value = double.TryParse(minutes, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) ? parsed : 10;
        var span = TimeSpan.FromMinutes(Math.Clamp(value, 0, 60 * 24 * 30));
        return span < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : span;
    }

    public static double Gate(string? text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gate) && double.IsFinite(gate) && gate is >= 0 and <= 1 ? gate : 0.75;

    // The model's {"items":[...]} or null when it is not that JSON. A fenced block is unwrapped; malformed items are skipped.
    public static Verdict[]? ParseVerdict(string modelText)
    {
        var text = (modelText ?? "").Trim();
        var open = text.IndexOf('{');
        var close = text.LastIndexOf('}');
        if (open < 0 || close < open) { return null; }
        try
        {
            using var document = JsonDocument.Parse(text[open..(close + 1)]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            { return null; }
            var parsed = new List<Verdict>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) { continue; }
                var body = Text(item, "text").Trim();
                string[] ids = item.TryGetProperty("episodeIds", out var list) && list.ValueKind == JsonValueKind.Array
                    ? [.. list.EnumerateArray().Where(id => id.ValueKind == JsonValueKind.String).Select(id => id.GetString() ?? "").Where(id => id.Length > 0)]
                    : [];
                if (body.Length == 0 || ids.Length == 0) { continue; }
                var contradicts = Text(item, "contradicts").Trim();
                parsed.Add(new(Text(item, "disposition").Trim().ToLowerInvariant(), ids, Text(item, "kind").Trim().ToLowerInvariant(), body,
                    Text(item, "subject").Trim(), Math.Clamp(Number(item, "confidence", 0), 0, 1),
                    (int)Math.Clamp(Math.Round(Number(item, "importance", 1)), 1, 10), Text(item, "reason").Trim(),
                    contradicts.Length == 0 || contradicts == "null" ? null : contradicts));
            }
            return [.. parsed];
        }
        catch (JsonException) { return null; }
    }

    private static string Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static double Number(JsonElement root, string name, double fallback)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && double.IsFinite(value.GetDouble()) ? value.GetDouble() : fallback;
}
