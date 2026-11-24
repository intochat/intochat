using System.Text.Json;
using DigitalBrain.Flows;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Apps;

// The Creator's brain graph (its select buttons command the view's own neuron): a sphere per unit, compiled app and kernel neuron, a ghost per unit under construction, an edge per
// reaction or gate labelled with its alias. The inspector reads three keys: `node:<id>` (a node's select button), `edge:<from>→<to>:<alias>`
// and `pulse:<traceId>`. Edges and pulses are not nodes of the view: the renderer activates SelectKey with their key as the value.
public static class BrainGraph
{
    public const string Unit = BrainTopologyNode.UnitRole, App = BrainTopologyNode.AppRole, Kernel = "kernel", Draft = "draft";
    private const double Radius = 320;

    public static string NodeKey(string id) => $"node:{id}";

    // The key of the view's button that selects an edge or a pulse; the renderer sends their key as its value.
    public const string SelectKey = "graph:select";

    // An edge key back into its parts: split on the arrow first (ids hold ':'), then on the last ':' (an alias holds none).
    public static (string From, string To, string Alias)? ParseEdge(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!key.StartsWith("edge:", StringComparison.Ordinal)) { return null; }
        var body = key["edge:".Length..];
        var arrow = body.IndexOf('→', StringComparison.Ordinal);
        if (arrow <= 0) { return null; }
        var rest = body[(arrow + 1)..];
        var colon = rest.LastIndexOf(':');
        return colon <= 0 || colon == rest.Length - 1 ? null : (body[..arrow], rest[..colon], rest[(colon + 1)..]);
    }

    public static string EdgeKey(string from, string to, string alias) => $"edge:{from}→{to}:{alias}";

    // A gate's label carries `#gate` after the alias, so a reaction and a gate on one alias stay two edges (and two keys); the
    // renderer matches pulses on the part before `#`.
    public const string GateSuffix = "#gate";

    public static string EdgeLabel(string alias, string kind) => kind == "gate" ? alias + GateSuffix : alias;

    // An installed unit's state neuron is `{brain}/{alias}/default`; its alias is the middle segment.
    public static string? AliasOf(string id)
    {
        var parts = id.Split('/');
        return parts.Length == 3 && parts[2] == "default" ? parts[1] : null;
    }

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
            if (kind == Unit && AliasOf(node.Id) is { } installed) { units.Add(installed); }
            members.Add((node.Id, node.Label, kind, null));
        }
        foreach (var flow in topology.Flows)
        {
            if (flow.Type != BuildInner.Flow || flow.Subject is not { Length: > 0 } alias || !flow.Done.Contains("author")) { continue; }
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
                Select: new Button(NodeKey(id), label, new Command(".", "select", JsonSerializer.SerializeToElement(NodeKey(id)))));
        }
        var synapses = topology.Edges
            .Where(edge => seen.Contains(edge.TargetId))
            .Select(edge => new BrainGraphSynapse(edge.SourceId, edge.TargetId, EdgeLabel(edge.SignalType, edge.Kind)))
            .Distinct()
            .ToArray();
        return new BrainSceneNode(nodes, synapses, selection is not null && seen.Contains(selection) ? selection : null);
    }
}
