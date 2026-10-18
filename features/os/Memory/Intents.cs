using System.Text;
using System.Text.Json;
using IntoChat.OS.AI.Agents.Intents;

// Shared by "intend": where Memory's rephrasing agent and its answers live, how the ask and the model's
// reply are read, and how the active intents are rendered for the rephrase prompt.
internal static class Intents
{
    public sealed record Ask(string Message, string RequestId, string Conversation);

    public sealed record Reply(IntentBrief Brief, double Confidence, Guid? Continues);

    public static string AgentKey(string appKey) => appKey + "/agents/intend";

    public static string ContextKey(string appKey) => appKey + "/context";

    public static string WorkingKey(string appKey) => appKey + "/working";

    public const int WorkingLimit = 4_000;

    public const int ActiveBudget = 6_000;

    public static string RenderWorking(string text)
        => (text ?? "").Length <= WorkingLimit ? text ?? "" : text![..WorkingLimit];

    public static string AnswerKey(string appKey, string requestId) => appKey + "/intents/" + requestId;

    public static Ask ParseAsk(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { throw new ArgumentException("The input is not a JSON object."); }
            var message = Text(root, "message");
            var requestId = Text(root, "requestId");
            if (message.Length == 0 || requestId.Length == 0) { throw new ArgumentException("The input needs a message and a requestId."); }
            return new(message, requestId, Text(root, "conversation"));
        }
        catch (JsonException error) { throw new ArgumentException("The input is not valid JSON: " + error.Message, error); }
    }

    private static string Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    // The model's text as the reply, or null when it is not the expected JSON. A fenced block is unwrapped.
    public static Reply? ParseReply(string modelText)
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
                || !root.TryGetProperty("brief", out var brief) || brief.ValueKind != JsonValueKind.Object
                || Text(brief, "goal").Trim() is not { Length: > 0 } goal
                || !root.TryGetProperty("confidence", out var confidence) || confidence.ValueKind != JsonValueKind.Number)
            { return null; }
            Guid? continues = root.TryGetProperty("continues", out var named) && named.ValueKind == JsonValueKind.String
                && Guid.TryParse(named.GetString(), out var id) ? id : null;
            return new(new(goal, List(brief, "inputs"), List(brief, "constraints"), List(brief, "acceptance")),
                Math.Clamp(confidence.GetDouble(), 0, 1), continues);
        }
        catch (JsonException) { return null; }
    }

    private static string[] List(JsonElement brief, string name)
        => brief.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)]
            : [];

    // An intent this request created and that never left Heard is orphaned by a failure: abandon it.
    public static bool ShouldCompensate(Intent? intent, string requestId)
        => intent is { State: IntentState.Heard } && intent.RequestId == requestId;

    // Active intents (not Done or Abandoned) as markdown, newest first; the intents arrive oldest first. Whole
    // intents are added while they fit the budget; the first that does not ends the list.
    public static string RenderActive(Intent[] intents, int budget)
    {
        var builder = new StringBuilder();
        foreach (var intent in intents.Reverse().Where(intent => intent.State is not (IntentState.Done or IntentState.Abandoned)))
        {
            var entry = "- `" + intent.Id + "` " + intent.State + ": " + (intent.Brief?.Goal ?? intent.Heard)
                + (intent.Brief?.Acceptance is { Length: > 0 } acceptance ? " (accepted when: " + acceptance[0] + ")" : "") + "\n";
            if (builder.Length + entry.Length > budget) { break; }
            builder.Append(entry);
        }
        return builder.ToString();
    }
}
