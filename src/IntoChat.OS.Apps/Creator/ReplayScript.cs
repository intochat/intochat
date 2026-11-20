using System.Globalization;
using System.Text.Json;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Creator;

// Replaying a trace: its signals in sequence order are the steps, ReplayCursor the index of the current one. The graph carries the
// steps to the brain scene as reserved synapses (label `replay:<index>:<seq>:<alias>:<traceId>`, from the publisher to the first node the alias
// reaches, Active on the current step): the scene never draws them as edges, it plays them as pulses while one is active.
public static class ReplayScript
{
    public const string Prefix = "replay:";
    public const string StateChangedAlias = "synapse.state-changed";

    public static TraceHop[] Steps(IReadOnlyList<TraceHop> hops)
    {
        ArgumentNullException.ThrowIfNull(hops);
        return [.. hops.OrderBy(h => h.Sequence)];
    }

    // The new cursor for a replay argument (first, prev, next, last, toggle, an index; stop or live leaves replay); null leaves replay.
    public static long? Move(long? cursor, string? argument, int count)
    {
        if (count <= 0) { return null; }
        var last = count - 1L;
        return argument?.Trim().ToLowerInvariant() switch
        {
            "first" or "home" => 0,
            "last" or "end" => last,
            "prev" or "arrowleft" or "left" => cursor is { } c ? Math.Max(c - 1, 0) : last,
            "next" or "arrowright" or "right" => cursor is { } n ? Math.Min(n + 1, last) : 0,
            "toggle" or "space" => cursor is null ? last : null,
            "stop" or "live" => null,
            { } text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                => Math.Clamp(index, 0, last),
            _ => cursor is { } kept ? Math.Clamp(kept, 0, last) : null,
        };
    }

    public static string FormatCursorReadout(long? cursor, int count)
    {
        if (count <= 0)
        {
            return "Event 0 / 0";
        }

        if (cursor is { } idx)
        {
            var clamped = Math.Clamp(idx, 0, count - 1) + 1;
            return string.Create(CultureInfo.InvariantCulture, $"Event {clamped} / {count}");
        }

        return string.Create(CultureInfo.InvariantCulture, $"Live · Event {count} / {count}");
    }

    public static string StateJsonAt(IReadOnlyList<TraceHop> steps, long? cursor, string? liveStateJson = null)
    {
        ArgumentNullException.ThrowIfNull(steps);

        if (cursor is { } idx && idx >= 0 && idx < steps.Count)
        {
            var stateAtStep = steps
                .Take((int)idx + 1)
                .LastOrDefault(s => s.Alias == StateChangedAlias && IsJsonObject(s.Payload));
            if (stateAtStep is not null)
            {
                return stateAtStep.Payload;
            }

            var hop = steps[(int)idx];
            return JsonSerializer.Serialize(new ReplayHopSnapshot(
                EventIndex: idx + 1,
                Sequence: hop.Sequence,
                Signal: hop.Alias,
                Publisher: hop.Publisher,
                Cause: hop.Cause ?? string.Empty,
                At: hop.At.ToString("O", CultureInfo.InvariantCulture),
                Payload: TryParseJsonElement(hop.Payload)), JsonSerializerOptions.Web);
        }

        if (!string.IsNullOrWhiteSpace(liveStateJson) && IsJsonObject(liveStateJson))
        {
            return liveStateJson;
        }

        if (steps.LastOrDefault(s => s.Alias == StateChangedAlias && IsJsonObject(s.Payload)) is { } latest)
        {
            return latest.Payload;
        }

        return "{}";
    }

    public static BrainGraphSynapse[] Synapses(IReadOnlyList<TraceHop> hops, string traceId, long? cursor, BrainTopology topology)
    {
        ArgumentNullException.ThrowIfNull(hops);
        ArgumentNullException.ThrowIfNull(topology);
        var steps = Steps(hops);
        var targets = topology.Edges
            .GroupBy(e => e.SignalType, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().TargetId, StringComparer.Ordinal);
        return [.. steps.Select((step, index) => new BrainGraphSynapse(
            step.Publisher,
            targets.GetValueOrDefault(step.Alias, ""),
            $"{Prefix}{index.ToString(CultureInfo.InvariantCulture)}:{step.Sequence.ToString(CultureInfo.InvariantCulture)}:{step.Alias}:{traceId}",
            Active: cursor == index))];
    }

    // What the state changed to at step [index]: its top-level properties against the previous StateChanged payload of the same
    // publisher in the trace. Rows are (property, before, after); null when the step is not a state change.
    public static string[][]? Diff(IReadOnlyList<TraceHop> steps, long index)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (index < 0 || index >= steps.Count || steps[(int)index] is not { Alias: StateChangedAlias } step) { return null; }
        var after = Properties(step.Payload);
        var before = steps
            .Take((int)index)
            .Where(s => s.Alias == StateChangedAlias && s.Publisher == step.Publisher)
            .Select(s => Properties(s.Payload))
            .LastOrDefault();
        if (before is null)
        {
            return [.. after.Keys.Order(StringComparer.Ordinal).Select(k => new[] { k, "", after[k] })];
        }

        return [.. after.Keys.Concat(before.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(k => before.GetValueOrDefault(k) != after.GetValueOrDefault(k))
            .Select(k => new[] { k, before.GetValueOrDefault(k, ""), after.GetValueOrDefault(k, "") })];
    }

    private static bool IsJsonObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) { return false; }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static object TryParseJsonElement(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) { return string.Empty; }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    private static Dictionary<string, string> Properties(string payload)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    properties[property.Name] = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? ""
                        : property.Value.GetRawText();
                }
            }
        }
        catch (JsonException) { return []; }
        return properties;
    }

    private sealed record ReplayHopSnapshot(
        long EventIndex,
        long Sequence,
        string Signal,
        string Publisher,
        string Cause,
        string At,
        object Payload);
}
