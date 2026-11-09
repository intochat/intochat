using System.Text.Json;
using IntoChat.OS.Modules.Apps.Trace;

namespace IntoChat.OS.Apps.Creator;

// A trace's journaled signals folded into a causation tree (what the Trace app did, now the inspector's pulse panel).
public static class TraceFold
{
    public const int MaxIndent = 12;

    // Depth first from each root in sequence order; a hop whose cause is not among the signals is a root.
    public static TraceHop[] Fold(IReadOnlyList<TraceEntry> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        var ordered = signals.OrderBy(s => s.Sequence).ToArray();
        var addresses = ordered.Select(AddressOf).ToHashSet(StringComparer.Ordinal);
        var children = ordered.Where(s => s.CausationId is { } c && addresses.Contains(c))
            .GroupBy(s => s.CausationId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var hops = new List<TraceHop>(ordered.Length);
        var stack = new Stack<(TraceEntry Signal, int Depth)>();
        foreach (var root in ordered.Where(s => s.CausationId is not { } c || !addresses.Contains(c)).Reverse()) { stack.Push((root, 0)); }
        while (stack.Count > 0)
        {
            var (signal, depth) = stack.Pop();
            var address = AddressOf(signal);
            hops.Add(new TraceHop(signal.Sequence, signal.Publisher, signal.Signal, address, signal.CausationId, depth, signal.At, signal.Payload,
                signal.Signal.StartsWith("flow.", StringComparison.Ordinal) ? FlowIdOf(signal.Payload) : null));
            if (children.TryGetValue(address, out var kids))
            {
                foreach (var kid in kids.Reverse()) { stack.Push((kid, depth + 1)); }
            }
        }
        return [.. hops];
    }

    // Two spaces per level, capped at MaxIndent levels; a deeper hop is marked with ».
    public static string Indent(int depth) => depth > MaxIndent ? new string(' ', 2 * MaxIndent) + "» " : new string(' ', 2 * depth);

    public static string AddressOf(TraceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return $"{entry.Publisher}:{entry.Sequence}";
    }

    private static string? FlowIdOf(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("flowId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
