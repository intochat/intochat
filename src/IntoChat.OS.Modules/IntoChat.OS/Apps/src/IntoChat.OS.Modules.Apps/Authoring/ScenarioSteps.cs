using System.Globalization;
using System.Net;
using System.Text.Json;
using DigitalBrain.Testing.Synapses;
using ScenarioTick = DigitalBrain.Testing.Synapses.Tick;

namespace IntoChat.OS.Modules.Apps;

// The one grammar an authored scenario is written in, and its reading into UnitScenario's fakes and steps.
//
// given (JSON object; every key optional):
//   {"http":  {"<slot>": {"<url part>": <body>, "*": <body>}},     a GET answers with the body of the longest url part the url
//                                                                  contains, else "*"; a url with neither fails the reaction;
//                                                                  <body> may be {"$each": [<body>, ...]} (one per GET, last repeats)
//                                                                  or {"$fail": <400..599|"timeout">} (alone or inside $each)
//    "tools": {"<slot>": {"<tool name>": <result>}}}               Run answers with the result named by the tool
// when (one step per line, in order):
//   tick <n><s|m|h|d>                                              the clock moves on by that much (ticks are cumulative)
//   publish <signal.alias> [<json>]                                journals the signal with the payload ({} when none)
//   then <expectation>                                             any then line, checked at that point
// then (one expectation per line, checked after the last when):
//   state <alias> <path> = <json>                                  the state at the dotted path (nested: A.B) equals the JSON value
//   signal <signal.alias>                                          the signal was journaled by the stimulus before it
//   never signal <signal.alias>                                    the signal was not journaled from the first step up to here
//   view <alias> contains "<text>"                                 some string value of the alias's published view contains the text
//   failed [<Function>]                                            a reaction failed on the stimulus before it
internal static class ScenarioSteps
{
    // UnitScenario's TestOS clock starts here; a tick is an offset from it.
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public sealed record Parsed(FakeSlots Fakes, IReadOnlyList<ScenarioStep> Steps);

    private sealed class HttpAnswers(Func<string>[] answers)
    {
        private int _calls;

        public string Next() => answers[Math.Min(Interlocked.Increment(ref _calls), answers.Length) - 1]();
    }

    public static Parsed Parse(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var steps = new List<ScenarioStep>();
        var at = Start;
        foreach (var line in scenario.When) { steps.Add(When(line.Trim(), ref at)); }
        foreach (var line in scenario.Then) { steps.Add(Then(line.Trim())); }
        return new Parsed(Fakes(scenario.Given), steps);
    }

    private static FakeSlots Fakes(JsonElement given)
    {
        var fakes = new FakeSlots();
        if (given.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) { return fakes; }
        if (given.ValueKind != JsonValueKind.Object) { throw new FormatException("given must be a JSON object with \"http\" and/or \"tools\"."); }
        foreach (var kind in given.EnumerateObject())
        {
            foreach (var slot in Slots(kind))
            {
                switch (kind.Name)
                {
                    case "http":
                        var http = slot.Value.EnumerateObject().ToDictionary(p => p.Name, p => HttpPart(slot.Name, p.Name, p.Value), StringComparer.Ordinal);
                        fakes.Http(slot.Name, url => Body(slot.Name, http, url));
                        break;
                    case "tools":
                        var tools = slot.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
                        fakes.Tool(slot.Name, (tool, _) => tools.TryGetValue(tool, out var result)
                            ? result
                            : throw new InvalidOperationException($"the scenario gives slot '{slot.Name}' no result for tool '{tool}'"));
                        break;
                    default:
                        throw new FormatException($"given has '{kind.Name}'; only \"http\" and \"tools\" are known.");
                }
            }
        }
        return fakes;
    }

    private static IEnumerable<JsonProperty> Slots(JsonProperty kind)
    {
        if (kind.Value.ValueKind != JsonValueKind.Object) { throw new FormatException($"given.{kind.Name} must be an object of slot names."); }
        foreach (var slot in kind.Value.EnumerateObject())
        {
            if (slot.Value.ValueKind != JsonValueKind.Object) { throw new FormatException($"given.{kind.Name}.{slot.Name} must be an object."); }
            yield return slot;
        }
    }

    private static HttpAnswers HttpPart(string slot, string part, JsonElement spec)
    {
        if (SingleKey(spec, out var key, out var inner) && key == "$each")
        {
            if (inner.ValueKind != JsonValueKind.Array || inner.GetArrayLength() == 0)
            { throw new FormatException($"given.http.{slot}.{part}: $each needs at least one body."); }
            return new HttpAnswers([.. inner.EnumerateArray().Select(item => HttpOne(slot, part, item))]);
        }
        return new HttpAnswers([HttpOne(slot, part, spec)]);
    }

    private static Func<string> HttpOne(string slot, string part, JsonElement body)
    {
        if (SingleKey(body, out var key, out var inner) && key == "$fail")
        {
            if (inner.ValueKind == JsonValueKind.Number && inner.TryGetInt32(out var status) && status is >= 400 and <= 599)
            {
                return () => throw new HttpRequestException($"Response status code does not indicate success: {status}.", null, (HttpStatusCode)status);
            }
            if (inner.ValueKind == JsonValueKind.String && inner.GetString() == "timeout")
            {
                return () => throw new TaskCanceledException("The request timed out.");
            }
            throw new FormatException($"given.http.{slot}.{part}: $fail is a status from 400 to 599 or \"timeout\".");
        }
        var text = body.ValueKind == JsonValueKind.String ? body.GetString()! : body.GetRawText();
        return () => text;
    }

    private static bool SingleKey(JsonElement element, out string key, out JsonElement value)
    {
        key = "";
        value = default;
        if (element.ValueKind != JsonValueKind.Object) { return false; }
        using var enumerator = element.EnumerateObject();
        if (!enumerator.MoveNext()) { return false; }
        var first = enumerator.Current;
        if (enumerator.MoveNext()) { return false; }
        key = first.Name;
        value = first.Value;
        return true;
    }

    private static string Body(string slot, Dictionary<string, HttpAnswers> answers, string url)
    {
        var match = answers.Keys.Where(part => part != "*" && url.Contains(part, StringComparison.Ordinal)).OrderByDescending(part => part.Length).FirstOrDefault()
            ?? (answers.ContainsKey("*") ? "*" : null)
            ?? throw new InvalidOperationException($"the scenario gives slot '{slot}' no body for {url}");
        return answers[match].Next();
    }

    private static ScenarioStep When(string line, ref DateTimeOffset at)
    {
        var (verb, rest) = Split(line);
        switch (verb)
        {
            case "tick":
                at += Duration(rest, line);
                return new ScenarioTick(at);
            case "publish":
                var (alias, payload) = Split(rest);
                if (alias.Length == 0) { throw new FormatException($"'{line}': publish names a signal alias."); }
                return new Publish(alias, Json(payload.Length == 0 ? "{}" : payload, line));
            case "then" when rest.Length > 0:
                return Then(rest);
            default:
                throw new FormatException($"'{line}': a when line is 'tick <duration>', 'publish <signal.alias> [json]' or 'then <expectation>'.");
        }
    }

    private static ScenarioStep Then(string line)
    {
        var (verb, rest) = Split(line);
        switch (verb)
        {
            case "signal" when rest.Length > 0 && !rest.Contains(' ', StringComparison.Ordinal):
                return new ExpectSignal(rest);
            case "state":
                var equals = rest.IndexOf('=', StringComparison.Ordinal);
                var (alias, path) = equals < 0 ? ("", "") : Split(rest[..equals].Trim());
                if (alias.Length == 0 || path.Length == 0 || path.Contains(' ', StringComparison.Ordinal))
                { throw new FormatException($"'{line}': a state line is 'state <alias> <path> = <json>'."); }
                return new ExpectState(alias, path, Json(rest[(equals + 1)..].Trim(), line));
            case "view":
                var (viewAlias, afterView) = Split(rest);
                var (contains, rawText) = Split(afterView);
                if (viewAlias.Length == 0 || contains != "contains" || !TryJsonString(rawText, out var viewText))
                { throw new FormatException($"'{line}': a view line is 'view <alias> contains \"<text>\"'."); }
                return new ExpectView(viewAlias, viewText);
            case "never":
                var (neverKind, neverAlias) = Split(rest);
                if (neverKind != "signal" || neverAlias.Length == 0 || neverAlias.Contains(' ', StringComparison.Ordinal))
                { throw new FormatException($"'{line}': a never line is 'never signal <signal.alias>'."); }
                return new ExpectNoSignal(neverAlias);
            case "failed":
                if (rest.Contains(' ', StringComparison.Ordinal))
                { throw new FormatException($"'{line}': a failed line is 'failed' or 'failed <Function>'."); }
                return new ExpectFailure(rest.Length == 0 ? null : rest);
            default:
                throw new FormatException($"'{line}': a then line is 'state <alias> <path> = <json>', 'signal <signal.alias>', 'view <alias> contains \"<text>\"', 'never signal <signal.alias>' or 'failed [<Function>]'.");
        }
    }

    private static bool TryJsonString(string text, out string value)
    {
        if (text.Length >= 2 && text[0] == '"')
        {
            try
            {
                if (JsonElement.Parse(text) is { ValueKind: JsonValueKind.String } parsed && parsed.GetString() is { } s)
                {
                    value = s;
                    return true;
                }
            }
            catch (JsonException) { }
        }
        value = "";
        return false;
    }

    private static TimeSpan Duration(string text, string line)
    {
        if (text.Length >= 2 && int.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
        {
            switch (text[^1])
            {
                case 's': return TimeSpan.FromSeconds(n);
                case 'm': return TimeSpan.FromMinutes(n);
                case 'h': return TimeSpan.FromHours(n);
                case 'd': return TimeSpan.FromDays(n);
            }
        }
        throw new FormatException($"'{line}': a tick is a positive whole number with s, m, h or d, such as 'tick 6m'.");
    }

    private static JsonElement Json(string text, string line)
    {
        try { return JsonElement.Parse(text); }
        catch (JsonException error) { throw new FormatException($"'{line}': '{text}' is not JSON ({error.Message})."); }
    }

    private static (string Head, string Tail) Split(string text)
    {
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..].Trim());
    }
}
