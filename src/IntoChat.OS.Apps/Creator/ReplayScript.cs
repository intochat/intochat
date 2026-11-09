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

    // The new cursor for a replay argument (first, prev, next, last, an index; stop or live leaves replay); null leaves replay.
    public static long? Move(long? cursor, string? argument, int count)
    {
        if (count == 0) { return null; }
        var last = count - 1L;
        return argument?.Trim() switch
        {
            "first" => 0,
            "last" => last,
            "prev" => cursor is { } c ? Math.Max(c - 1, 0) : last,
            "next" => cursor is { } n ? Math.Min(n + 1, last) : 0,
            "stop" or "live" => null,
            { } text when long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var index) => Math.Min(index, last),
            _ => cursor is { } kept ? Math.Min(kept, last) : null,
        };
    }

    public static BrainGraphSynapse[] Synapses(IReadOnlyList<TraceHop> hops, string traceId, long? cursor, BrainTopology topology)
    {
        ArgumentNullException.ThrowIfNull(hops);
        ArgumentNullException.ThrowIfNull(topology);
        var steps = Steps(hops);
        var targets = topology.Edges.GroupBy(e => e.SignalType, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().TargetId, StringComparer.Ordinal);
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
        var before = steps.Take((int)index).Where(s => s.Alias == StateChangedAlias && s.Publisher == step.Publisher).Select(s => Properties(s.Payload)).LastOrDefault()
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        return [.. after.Keys.Concat(before.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(k => before.GetValueOrDefault(k) != after.GetValueOrDefault(k))
            .Select(k => new[] { k, before.GetValueOrDefault(k, ""), after.GetValueOrDefault(k, "") })];
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
                    properties[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.GetRawText();
                }
            }
        }
        catch (JsonException) { return []; }
        return properties;
    }
}
