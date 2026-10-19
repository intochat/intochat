using System.Text.Json;
using IntoChat.OS.Postgres.Tables;

// Shared by "capture": the episodes table, the id of an episode, how the ask and the model's summary are read,
// and the bounded fallback used when the model gives none.
internal static class Episodes
{
    public sealed record Ask(string Agent, string RunId);

    public sealed record Note(string Summary, int Importance, bool IsFallback);

    // Frozen once created: a new shape takes a new key.
    public static readonly TableDefinition Definition = new(
    [
        new("id", "text"), new("agent", "text"), new("run", "text"), new("at", "timestamptz"), new("summary", "text"),
        new("importance", "number"), new("fallback", "boolean"), new("consolidated_at", "timestamptz"),
        new("claim", "text"), new("claim_until", "timestamptz"),
    ], ["id"]);

    public const int FallbackLength = 300;

    public static string TableKey(string appKey) => appKey + "/episodes";

    public static string AgentKey(string appKey) => appKey + "/agents/capture";

    public static string Id(string agent, string run) => agent + "#" + run;

    public static Ask ParseAsk(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { throw new ArgumentException("The input is not a JSON object."); }
            var agent = Text(root, "agent");
            var runId = Text(root, "runId");
            if (agent.Length == 0 || runId.Length == 0) { throw new ArgumentException("The input needs an agent and a runId."); }
            return new(agent, runId);
        }
        catch (JsonException error) { throw new ArgumentException("The input is not valid JSON: " + error.Message, error); }
    }

    private static string Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    // The model's text as (summary, importance 1..10), or null when it is not the expected JSON. A fenced block is unwrapped.
    public static (string Summary, int Importance)? ParseSummary(string modelText)
    {
        var text = (modelText ?? "").Trim();
        var open = text.IndexOf('{');
        var close = text.LastIndexOf('}');
        if (open < 0 || close < open) { return null; }
        try
        {
            using var document = JsonDocument.Parse(text[open..(close + 1)]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Text(root, "summary").Trim() is not { Length: > 0 } summary
                || !root.TryGetProperty("importance", out var importance) || importance.ValueKind != JsonValueKind.Number)
            { return null; }
            return (summary, (int)Math.Clamp(Math.Round(importance.GetDouble()), 1, 10));
        }
        catch (JsonException) { return null; }
    }

    // The first 300 characters of each side, on a surrogate boundary, importance 3, marked as a fallback.
    public static Note Fallback(string? userText, string? reply)
        => new("- The user said: " + Cut(userText) + "\n- The reply was: " + Cut(reply), 3, true);

    private static string Cut(string? text)
    {
        text = (text ?? "").Trim();
        if (text.Length <= FallbackLength) { return text; }
        var end = char.IsHighSurrogate(text[FallbackLength - 1]) ? FallbackLength - 1 : FallbackLength;
        return text[..end];
    }
}
