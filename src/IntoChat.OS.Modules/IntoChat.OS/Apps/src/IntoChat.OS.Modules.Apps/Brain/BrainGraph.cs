using System.Text.Json;
using DigitalBrain.Flows;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Apps;

// One node of the brain's topology as the brain-snapshot stream lists it: Role is "unit" for an installed unit's state neuron,
// "app" for a compiled app, anything else a kernel neuron.
public sealed record BrainTopologyNode(string Id, string Label, string Role);

// One reaction or gate: SourceId "*" means any publisher of SignalType (the alias); the journal tail names who published.
public sealed record BrainTopologyEdge(string SourceId, string TargetId, string SignalType, string Kind);

public sealed record BrainTopology(IReadOnlyList<BrainTopologyNode> Nodes, IReadOnlyList<BrainTopologyEdge> Edges)
{
    // Build flows under way; one past `author` whose subject has no installed unit yet is drawn as a ghost.
    public IReadOnlyList<FlowView> Flows { get; init; } = [];
}

// The Creator's brain graph: a sphere per unit, compiled app and kernel neuron, a ghost per unit under construction, an edge per
// reaction or gate labelled with its alias. The inspector reads three keys: `node:<id>` (a node's select button), `edge:<from>→<to>:<alias>`
// and `pulse:<traceId>` (activated by the renderer, which owns edges and pulses on screen).
public static class BrainGraph
{
    public const string Unit = "unit", App = "app", Kernel = "kernel", Draft = "draft";
    private const double Radius = 320;

    public static string NodeKey(string id) => $"node:{id}";

    public static string EdgeKey(string from, string to, string alias) => $"edge:{from}→{to}:{alias}";

    public static string PulseKey(string traceId) => $"pulse:{traceId}";

    public static string DraftId(string alias) => $"draft:{alias}";

    public static BrainSceneNode Build(BrainTopology topology, string? selection)
    {
        ArgumentNullException.ThrowIfNull(topology);
        List<(string Id, string Label, string Kind, string? Subtitle)> members = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        HashSet<string> units = new(StringComparer.Ordinal);
        foreach (var node in topology.Nodes)
        {
            if (!seen.Add(node.Id)) { continue; }
            var kind = node.Role switch { Unit => Unit, App => App, _ => Kernel };
            if (kind == Unit) { units.Add(node.Label); }
            members.Add((node.Id, node.Label, kind, null));
        }
        foreach (var flow in topology.Flows)
        {
            if (flow.Type != BuildFlow.Alias || flow.Subject is not { Length: > 0 } alias || !flow.Done.Contains("author")) { continue; }
            if (flow.Status is FlowStatus.Failed or FlowStatus.Cancelled || units.Contains(alias)) { continue; }
            var id = DraftId(alias);
            if (seen.Add(id)) { members.Add((id, alias, Draft, flow.Current)); }
        }
        var nodes = new BrainGraphNode[members.Count];
        for (var i = 0; i < members.Count; i++)
        {
            var (id, label, kind, subtitle) = members[i];
            var angle = members.Count == 1 ? 0 : 2 * Math.PI * i / members.Count;
            var radius = members.Count == 1 ? 0 : Radius;
            nodes[i] = new BrainGraphNode(id, label, $"neuron://{id}", kind,
                Math.Round(radius * Math.Cos(angle), 2), Math.Round(radius * Math.Sin(angle), 2), 0,
                Status: kind == Draft ? "building" : "idle", Subtitle: subtitle,
                Select: new Button(NodeKey(id), label, new Emit("brain", "focus", JsonSerializer.SerializeToElement(id))));
        }
        var synapses = topology.Edges
            .Where(edge => seen.Contains(edge.TargetId))
            .Select(edge => new BrainGraphSynapse(edge.SourceId, edge.TargetId, edge.SignalType))
            .Distinct()
            .ToArray();
        return new BrainSceneNode(nodes, synapses, selection is not null && seen.Contains(selection) ? selection : null);
    }
}
