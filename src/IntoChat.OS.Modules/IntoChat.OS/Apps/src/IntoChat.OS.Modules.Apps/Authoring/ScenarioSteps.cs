using System.Globalization;
using System.Text.Json;
using DigitalBrain.Testing.Synapses;
using ScenarioTick = DigitalBrain.Testing.Synapses.Tick;

namespace IntoChat.OS.Modules.Apps;

// The one grammar an authored scenario is written in, and its reading into UnitScenario's fakes and steps.
//
// given (JSON object; every key optional):
//   {"http":  {"<slot>": {"<url part>": <body>, "*": <body>}},     a GET answers with the body of the longest url part the url
//                                                                  contains, else "*"; a url with neither fails the reaction
//    "tools": {"<slot>": {"<tool name>": <result>}}}               Run answers with the result named by the tool
// when (one stimulus per line, in order):
//   tick <n><s|m|h|d>                                              the clock moves on by that much (ticks are cumulative)
//   publish <signal.alias> [<json>]                                journals the signal with the payload ({} when none)
// then (one expectation per line, checked after every when):
//   state <alias> <path> = <json>                                  the state at the dotted path equals the JSON value
//   signal <signal.alias>                                          the signal was journaled by the last stimulus
internal static class ScenarioSteps
{
    // UnitScenario's TestOS clock starts here; a tick is an offset from it.
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public sealed record Parsed(FakeSlots Fakes, IReadOnlyList<ScenarioStep> Steps);

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
                var answers = slot.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
                switch (kind.Name)
                {
                    case "http":
                        fakes.Http(slot.Name, url => Body(slot.Name, answers, url));
                        break;
                    case "tools":
                        fakes.Tool(slot.Name, (tool, _) => answers.TryGetValue(tool, out var result)
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

    private static string Body(string slot, Dictionary<string, JsonElement> answers, string url)
    {
        var match = answers.Keys.Where(part => part != "*" && url.Contains(part, StringComparison.Ordinal)).OrderByDescending(part => part.Length).FirstOrDefault()
            ?? (answers.ContainsKey("*") ? "*" : null)
            ?? throw new InvalidOperationException($"the scenario gives slot '{slot}' no body for {url}");
        var body = answers[match];
        return body.ValueKind == JsonValueKind.String ? body.GetString()! : body.GetRawText();
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
            default:
                throw new FormatException($"'{line}': a when line is 'tick <duration>' or 'publish <signal.alias> [json]'.");
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
            default:
                throw new FormatException($"'{line}': a then line is 'state <alias> <path> = <json>' or 'signal <signal.alias>'.");
        }
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
