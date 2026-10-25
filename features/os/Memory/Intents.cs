using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using IntoChat.OS;
using IntoChat.OS.Modules.AI.Agents.Facts;
using IntoChat.OS.Modules.AI.Agents.Intents;

// Shared by "intend": where Memory's rephrasing agent and its answers live, how the ask and the model's
// reply are read, and how the active intents are rendered for the rephrase prompt.
internal static class Intents
{
    public sealed record Ask(string Message, string RequestId, string Conversation);

    public sealed record Reply(IntentBrief Brief, double Confidence, Guid? Continues);

    public static string AgentKey(string appKey) => appKey + "/agents/intend";

    public static string ContextKey(string appKey) => appKey + "/context";

    public static string ReminderKey(string appKey, Guid intentId) => appKey + "/cues/" + intentId;

    // A signal cue matches by alias alone, or by alias and one top-level property holding exactly EqualTo as a string.
    // A plain neuron's signal (06-05) is a [Signal("alias")] record; a grain's carries its alias as before.
    public static bool CueMatches(Cue cue, object signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var alias = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<DigitalBrain.SignalAttribute>(signal.GetType())?.Alias
            ?? NeuronAliases.Of(signal.GetType());
        if (cue.SignalAlias != alias) { return false; }
        if (cue.Field is not { Length: > 0 } field) { return true; }
        var json = JsonSerializer.SerializeToElement(signal, signal.GetType());
        if (json.ValueKind != JsonValueKind.Object) { return false; }
        foreach (var property in json.EnumerateObject())
        {
            if (string.Equals(property.Name, field, StringComparison.OrdinalIgnoreCase))
            { return property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == cue.EqualTo; }
        }
        return false;
    }

    // Alias to type, built by load on first use. A miss reloads once; the same miss is not retried until stamp
    // (the loaded-assembly count) changes, so an alias nobody will ever define costs one scan, not one per lookup.
    public sealed class CueTypes(Func<IReadOnlyDictionary<string, Type>> load, Func<int> stamp)
    {
        private IReadOnlyDictionary<string, Type>? map;
        private readonly Dictionary<string, int> missed = [];

        public Type? Resolve(string alias)
        {
            map ??= load();
            if (map.TryGetValue(alias, out var type)) { return type; }
            Refresh(alias);
            return map.GetValueOrDefault(alias);
        }

        // Reloads unless the same reason already missed at this stamp; true when it reloaded.
        private bool Refresh(string reason)
        {
            var now = stamp();
            lock (missed)
            {
                if (missed.TryGetValue(reason, out var at) && at == now) { return false; }
                missed[reason] = now;
            }
            // Unsynchronised on purpose: concurrent loads are idempotent and the reference write is atomic.
            map = load();
            return true;
        }
    }

    public const int FireAttempts = 3;

    public static bool IsWaiting(Intent? intent) => intent is { State: IntentState.Waiting };

    // Only a revision conflict is worth another read; attempts are counted from one.
    public static bool ShouldRetry(Exception error, int attempt) => error is RevisionConflictException && attempt < FireAttempts;

    // The answer written under the request id once the intent is rephrased: "ok <intent id>".
    public static string Ok(Guid intentId) => "ok " + intentId;

    // Confirming follows a confident rephrase only when the rephrase left the intent Rephrased: a continued
    // intent that was Dispatched or Waiting keeps its state and is not confirmed again.
    public static bool ShouldConfirm(Intent? intent, double confidence, double gate)
        => confidence >= gate && intent is { State: IntentState.Rephrased };

    public static string WorkingKey(string appKey) => appKey + "/working";

    public const int WorkingLimit = 4_000;

    public const int ActiveBudget = 6_000;

    public enum Change { Skip, Set, Remove }

    // What to do with a context entry: remove it when nothing is rendered, set it when the text differs.
    public static Change Decide(string? current, string rendered)
        => rendered.Length == 0 ? (current is null ? Change.Skip : Change.Remove)
            : current == rendered ? Change.Skip : Change.Set;

    // Cut at WorkingLimit characters, on a text-element boundary so a surrogate pair is never split.
    public static string RenderWorking(string text)
    {
        text ??= "";
        return text.Length <= WorkingLimit ? text : text[..CutAt(text, WorkingLimit)];
    }

    // The largest text-element start at or before limit.
    private static int CutAt(string text, int limit)
    {
        var cut = 0;
        foreach (var start in StringInfo.ParseCombiningCharacters(text))
        {
            if (start > limit) { break; }
            cut = start;
        }
        return cut;
    }

    // One physical line: CR, LF and tabs become single spaces so text cannot break a list or a prompt.
    public static string Line(string text) => Regex.Replace(text ?? "", "[\r\n\t]+", " ");

    private static readonly IntentState[] ActiveStates = [IntentState.Rephrased, IntentState.Confirmed, IntentState.Dispatched, IntentState.Waiting];

    public static Intent[] ActiveOf(Intent[] intents) => [.. intents.Where(intent => ActiveStates.Contains(intent.State))];

    public const int ProfileBudget = 3_000;

    // The facts the profile block shows: kind Profile, still valid and not hidden (what IFacts.Current() returns).
    public static Fact[] ProfileOf(Fact[] facts)
        => [.. facts.Where(fact => fact.Kind == FactKind.Profile && fact.ValidTo is null && !fact.Hidden)];

    // Profile facts as "- text" lines, newest recorded first; a line that does not fit is skipped and smaller ones still render.
    public static string RenderProfile(Fact[] facts, int budget)
    {
        var builder = new StringBuilder();
        foreach (var fact in facts.OrderByDescending(fact => fact.Recorded))
        {
            var entry = "- " + Line(fact.Text) + "\n";
            if (builder.Length + entry.Length > budget) { continue; }
            builder.Append(entry);
        }
        return builder.ToString();
    }

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
            var conversation = Text(root, "conversation");
            if (message.Length == 0 || requestId.Length == 0 || conversation.Length == 0)
            { throw new ArgumentException("The input needs a message, a requestId and a conversation."); }
            return new(message, requestId, conversation);
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
            var entry = "- `" + intent.Id + "` " + intent.State + ": " + Line(intent.Brief?.Goal ?? intent.Heard)
                + (intent.Brief?.Acceptance is { Length: > 0 } acceptance ? " (accepted when: " + Line(acceptance[0]) + ")" : "") + "\n";
            // A single intent over the whole budget is cut with an ellipsis rather than dropped.
            if (builder.Length == 0 && entry.Length > budget && budget > 2) { entry = entry[..CutAt(entry, budget - 2)] + "…\n"; }
            if (builder.Length + entry.Length > budget) { break; }
            builder.Append(entry);
        }
        return builder.ToString();
    }
}
