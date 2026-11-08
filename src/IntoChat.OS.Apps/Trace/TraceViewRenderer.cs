using System.Globalization;
using System.Text.Json;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps.Trace;

namespace IntoChat.OS.Apps.Trace;

public static class TraceViewRenderer
{
    public const string App = "trace";
    public const string BeforeTracing = "before tracing: this journal has no trace ids";

    // Folds the journaled signals into a causation tree, depth first from each root in sequence order.
    // A hop whose cause is not among the signals is a root.
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
            hops.Add(Hop(signal, address, depth));
            if (children.TryGetValue(address, out var kids))
            {
                foreach (var kid in kids.Reverse()) { stack.Push((kid, depth + 1)); }
            }
        }
        return [.. hops];
    }

    // The hop the replay bar stands on: the latest when live, else the selected one (or the latest when it is gone).
    public static long? Selected(TraceState state, TraceHop[] hops)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(hops);
        if (hops.Length == 0) { return null; }
        var last = hops.Max(h => h.Sequence);
        return state.Live || state.SelectedSequence is not { } selected || hops.All(h => h.Sequence != selected) ? last : selected;
    }

    // The previous (step -1) or next (step +1) hop in sequence order, clamped at the ends.
    public static long? Step(TraceState state, TraceHop[] hops, int step)
    {
        if (Selected(state, hops) is not { } current) { return null; }
        var sequences = hops.Select(h => h.Sequence).Order().ToArray();
        var index = Math.Clamp(Array.IndexOf(sequences, current) + step, 0, sequences.Length - 1);
        return sequences[index];
    }

    public static double ProgressValue(long selected, long floor, long last)
        => last == floor ? 0 : Math.Clamp((double)(selected - floor) / (last - floor), 0, 1);

    public static View Render(TraceState state, TraceHop[] hops, bool untraced)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(hops);
        if (hops.Length == 0 || untraced)
        {
            return new View("Trace", new Column([new InfoBar("No trace", BeforeTracing, "warning")]));
        }
        var selected = Selected(state, hops);
        var first = hops.Min(h => h.At);
        var table = new DataTable(
            $"Trace {state.TraceId}",
            ["#", "Δt", "Publisher", "Signal", "Cause"],
            [.. hops.Select(h => (IReadOnlyList<string>)[
                h.Sequence.ToString(CultureInfo.InvariantCulture),
                string.Create(CultureInfo.InvariantCulture, $"+{(h.At - first).TotalMilliseconds:0}ms"),
                new string(' ', 2 * h.Depth) + h.Publisher,
                h.Alias,
                h.Cause ?? "",
            ])]);
        var floor = hops.Min(h => h.Sequence);
        var last = hops.Max(h => h.Sequence);
        var center = new SplitPane(table, Graph(hops, selected), 420);
        return new View("Trace", new Column(
        [
            new SplitPane(center, Side(hops.FirstOrDefault(h => h.Sequence == selected)), 820),
            ReplayBar(state, selected ?? floor, floor, last),
        ]));
    }

    // One node per distinct publisher, one synapse per causation link in sequence order; the link into the selected hop is lit.
    public static BrainSceneNode Graph(TraceHop[] hops, long? selected)
    {
        ArgumentNullException.ThrowIfNull(hops);
        var publishers = hops.OrderBy(h => h.Sequence).Select(h => h.Publisher).Distinct(StringComparer.Ordinal).ToArray();
        var nodes = publishers.Select((p, i) =>
        {
            var angle = 2 * Math.PI * i / Math.Max(1, publishers.Length);
            return new BrainGraphNode(p, p, $"neuron://{p}", "neuron", Math.Cos(angle) * 10, Math.Sin(angle) * 10, 0);
        }).ToArray();
        var byAddress = hops.ToDictionary(h => h.Address, StringComparer.Ordinal);
        var synapses = hops.OrderBy(h => h.Sequence)
            .Where(h => h.Cause is { } c && byAddress.ContainsKey(c))
            .Select(h => new BrainGraphSynapse(byAddress[h.Cause!].Publisher, h.Publisher, h.Alias, Active: h.Sequence == selected))
            .ToArray();
        return new BrainSceneNode(nodes, synapses, hops.FirstOrDefault(h => h.Sequence == selected)?.Publisher);
    }

    public static Node ReplayBar(TraceState state, long selected, long floor, long last)
    {
        ArgumentNullException.ThrowIfNull(state);
        Button B(string label, string operation, string icon)
            => new(label, new ButtonAction($"apps/{App}", operation, []), Tone: label == "Live" && state.Live ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true, Icon = icon };
        return new Column(
        [
            new Row([B("Play", "play", "play"), B("Pause", "pause", "pause"), B("Back", "back", "skip-back"), B("Forward", "forward", "skip-forward"), B("Live", "live", "radio")]),
            new Progress("Replay", ProgressValue(selected, floor, last),
                $"hop {selected} of {floor}..{last} · {(state.Live ? "live" : "paused")} · Play follows live; step with Back and Forward"),
        ]);
    }

    public static Node Side(TraceHop? hop)
    {
        if (hop is null) { return new Card("Hop", "No hop selected.", []); }
        List<Node> children = [];
        if (hop.FlowId is { } flowId)
        {
            children.Add(new Button("open the flow", new ButtonAction("apps/tasks", "open", [flowId])) { Small = true, Icon = "tasks" });
        }
        return new Card($"{hop.Alias} · #{hop.Sequence}", $"{hop.Publisher}\n{hop.Payload}", children);
    }

    public static string AddressOf(TraceEntry entry) => $"{entry.Publisher}:{entry.Sequence}";

    public static TraceHop Hop(TraceEntry entry, string address, int depth)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new TraceHop(entry.Sequence, entry.Publisher, entry.Signal, address, entry.CausationId, depth, entry.At, entry.Payload,
            entry.Signal.StartsWith("flow.", StringComparison.Ordinal) ? FlowIdOf(entry.Payload) : null);
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
