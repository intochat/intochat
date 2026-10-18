using System.Globalization;
using System.Text;
using System.Text.Json;
using IntoChat.OS.Flutter.Text;

// The timers the router started and the ticks it heard, as two line ledgers, and the "Timer" text part
// derived from them. A started timer is one line in the timers neuron (written by ask.cs); a tick is one
// line in the timer-rang neuron (written by timer-done.cs); the text part is rebuilt from both by Show, which
// each writer calls after its own write and timer-view.cs calls on every change, so the part converges even
// when one change signal is late or lost.
// A tick counts only for a timer it can belong to: the same key, no earlier than the timer's due time
// after it was requested, and before a later timer on that key replaced its schedule. So a tick that
// arrives late, twice, or from a timer that was replaced never says a running timer is up.
public static class Timers
{
    public const int Kept = 16;
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(1);

    public sealed record Started(string Id, string Key, DateTimeOffset Requested, DateTimeOffset Answered, TimeSpan Due, TimeSpan? Period);

    public sealed record Rang(string Key, DateTimeOffset At);

    public static string[] Lines(string markdown) => [.. markdown.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0)];

    public static string Line(Started timer) => string.Join('\t', timer.Id, timer.Key, timer.Requested.ToString("O"), timer.Answered.ToString("O"),
        timer.Due.Ticks.ToString(CultureInfo.InvariantCulture), timer.Period?.Ticks.ToString(CultureInfo.InvariantCulture) ?? "-");

    public static string Line(Rang tick) => tick.Key + "\t" + tick.At.ToUniversalTime().ToString("O");

    public static Started[] StartedIn(string markdown) => [.. Lines(markdown).Select(line => line.Split('\t')).Where(cells => cells.Length == 6)
        .Select(cells => new Started(cells[0], cells[1], Moment(cells[2]), Moment(cells[3]), TimeSpan.FromTicks(long.Parse(cells[4], CultureInfo.InvariantCulture)),
            cells[5] == "-" ? null : TimeSpan.FromTicks(long.Parse(cells[5], CultureInfo.InvariantCulture))))];

    public static Rang[] RangIn(string markdown) => [.. Lines(markdown).Select(line => line.Split('\t')).Where(cells => cells.Length == 2)
        .Select(cells => new Rang(cells[0], Moment(cells[1])))];

    // The timers worth hearing: the keys of the timers started last, inside this brain only.
    public static string[] Keys(string markdown, string scope) => [.. StartedIn(markdown).Select(timer => timer.Key)
        .Where(key => key == scope || key.StartsWith(scope + "/", StringComparison.Ordinal)).Reverse().Distinct(StringComparer.Ordinal).Take(8)];

    // The routed call's arguments: [dueTime, period?] as TimeSpan strings.
    public static (TimeSpan Due, TimeSpan? Period) Durations(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) { return (TimeSpan.Zero, null); }
        using var document = JsonDocument.Parse(arguments);
        var values = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().ToArray() : [];
        return (values.Length > 0 ? Span(values[0]) ?? TimeSpan.Zero : TimeSpan.Zero, values.Length > 1 ? Span(values[1]) : null);
    }

    // Rebuilds the "Timer" text part from the two ledgers; writes only when it changed.
    public static async Task Show(IntoChatOSConnection brain)
    {
        var shown = Render((await brain.Get<IText>(Jev.Name(brain, "timers")).Read()).Markdown,
            (await brain.Get<IText>(Jev.Name(brain, "timer-rang")).Read()).Markdown);
        var part = brain.Get<IText>(Jev.Name(brain, "timer"));
        if (shown.Length > 0 && (await part.Read()).Markdown != shown) { await part.Set(shown); }
    }

    // Newest timer first: its duration and when it rings, or that its time is up; earlier ones as a list.
    public static string Render(string startedMarkdown, string rangMarkdown)
    {
        var started = StartedIn(startedMarkdown);
        if (started.Length == 0) { return ""; }
        var rang = RangIn(rangMarkdown);
        var builder = new StringBuilder(Headline(started, rang, started.Length - 1));
        var earlier = Enumerable.Range(0, started.Length - 1).Reverse().Take(4).ToArray();
        if (earlier.Length > 0)
        {
            builder.Append("\n\nEarlier:");
            foreach (var index in earlier) { builder.Append("\n- ").Append(Summary(started, rang, index)); }
        }
        return builder.ToString();
    }

    private static string Headline(Started[] started, Rang[] rang, int index)
    {
        var timer = started[index];
        var heard = Heard(started, rang, index);
        if (timer.Period is null && heard.Length > 0)
        { return $"**Time is up** · the timer for {Describe(timer.Due)} rang at {heard[0]:HH:mm:ss} UTC.\n\n`{timer.Key}`"; }
        var text = "**Timer** · " + Describe(timer.Due) + (timer.Period is { } every ? ", then every " + Describe(every) : "")
            + $"\n\nSet at {timer.Answered:HH:mm:ss} UTC, rings by {timer.Answered + timer.Due:HH:mm:ss} UTC.";
        if (heard.Length > 0) { text += $" Last rang at {heard[^1]:HH:mm:ss} UTC."; }
        return text + $"\n\n`{timer.Key}`";
    }

    private static string Summary(Started[] started, Rang[] rang, int index)
    {
        var timer = started[index];
        var heard = Heard(started, rang, index);
        var what = Describe(timer.Due) + (timer.Period is { } every ? ", then every " + Describe(every) : "");
        var state = heard.Length > 0 && timer.Period is null ? $"rang at {heard[0]:HH:mm:ss} UTC"
            : ReplacedBy(started, index) is not null ? "replaced by a later timer on the same key"
            : heard.Length > 0 ? $"last rang at {heard[^1]:HH:mm:ss} UTC"
            : $"running, rings by {timer.Answered + timer.Due:HH:mm:ss} UTC";
        return $"{what} · {state} `{timer.Key}`";
    }

    // The ticks that can be this timer's: its key, not before its due time from the request, and before a
    // later timer on the same key took over the schedule.
    private static DateTimeOffset[] Heard(Started[] started, Rang[] rang, int index)
    {
        var timer = started[index];
        var replaced = ReplacedBy(started, index);
        return [.. rang.Where(tick => tick.Key == timer.Key && tick.At >= timer.Requested + timer.Due - Skew
                && (replaced is null || tick.At < replaced.Answered))
            .Select(tick => tick.At).Distinct().Order()];
    }

    private static Started? ReplacedBy(Started[] started, int index)
        => started.Skip(index + 1).FirstOrDefault(later => later.Key == started[index].Key);

    public static string Describe(TimeSpan span)
    {
        var parts = new List<string>();
        if (span.Days > 0) { parts.Add(span.Days + (span.Days == 1 ? " day" : " days")); }
        if (span.Hours > 0) { parts.Add(span.Hours + (span.Hours == 1 ? " hour" : " hours")); }
        if (span.Minutes > 0) { parts.Add(span.Minutes + (span.Minutes == 1 ? " minute" : " minutes")); }
        if (span.Seconds > 0 || parts.Count == 0) { parts.Add(span.Seconds + (span.Seconds == 1 ? " second" : " seconds")); }
        return string.Join(" ", parts);
    }

    private static TimeSpan? Span(JsonElement value)
        => value.ValueKind == JsonValueKind.String && TimeSpan.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var span) ? span : null;

    private static DateTimeOffset Moment(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None);
}
