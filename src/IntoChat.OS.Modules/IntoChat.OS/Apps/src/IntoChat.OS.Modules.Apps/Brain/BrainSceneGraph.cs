using System.Globalization;
using DigitalBrain;
using DigitalBrain.Graph;
using IntoChat.OS.Modules.Flame.Scene;
using DigitalBrain.Signals.Watch;

namespace IntoChat.OS.Modules.Apps;

// The brain app over the brain graph: one level at a time, groups on a ring, members on a small circle around their group,
// a tap on a node drills down (brain → cluster → neurons) and a tap on the focused node goes back up.
public static partial class BrainSceneGraph
{
    public const string DrillParameter = "drill";
    public const string DrillUp = "brain";
    private const double GroupRadius = 240;
    private const double OuterRadius = 380;
    private const int MaxNodes = 1024;

    public static SceneDefinition Compose(
        GraphSnapshot graph,
        GraphLevel level,
        string? focus,
        IReadOnlyList<SignalDelivery> trail,
        double zoom,
        IReadOnlyList<AppNavigated>? navigations = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(trail);
        var count = Math.Min(graph.NodeCount, MaxNodes);
        var pulsing = FindPulsingNodes(graph, count);
        var positions = Layout(graph, count);
        var at = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        var nodes = new List<SceneNode>(count * 2);
        var sceneId = new string?[count];
        for (var i = 0; i < count; i++)
        {
            if (graph.Kinds[i] == GraphNodeKind.Brain) { continue; }
            var id = graph.Ids[i];
            var (x, y) = positions[i];
            at[id] = (x, y);
            sceneId[i] = "n" + i.ToString(CultureInfo.InvariantCulture);
            var group = graph.Group[i] >= 0 && graph.Group[i] < graph.Groups.Length ? graph.Groups[graph.Group[i]] : null;
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["icon"] = IconOf(graph.Kinds[i]),
                ["title"] = graph.Labels[i],
                ["description"] = Describe(graph, i, group),
                ["graphId"] = id,
                ["kind"] = graph.Kinds[i].ToString().ToLowerInvariant(),
                ["level"] = level.ToString().ToLowerInvariant(),
                ["neuron"] = id,
            };
            if (pulsing.Contains(i))
            {
                parameters["pulse"] = "true";
                parameters["activity"] = "working";
            }
            if (group is not null) { parameters["group"] = group.Label; }
            if (Drill(level, focus, id, graph.Kinds[i], group) is { } drill) { parameters[DrillParameter] = drill; }
            nodes.Add(new SceneNode(sceneId[i]!, "sphere", new Transform(x, y), null, parameters, [], [], ["tap"]));
        }

        var resolved = trail.Take(BrainScene.MaxTrailSteps)
            .Select(delivery => (Publisher: Resolve(graph, at, delivery.From), Subscriber: Resolve(graph, at, delivery.To)))
            .ToArray();
        var edge = 0;
        for (var from = 0; from < count; from++)
        {
            if (sceneId[from] is null) { continue; }
            for (var e = graph.RowStart[from]; e < graph.RowStart[from + 1]; e++)
            {
                var to = graph.Col[e];
                if (to >= count || sceneId[to] is null || nodes.Count >= Flame.FlameVocabulary.MaxNodes - 1) { continue; }
                var kind = graph.EdgeKinds[e];
                var a = at[graph.Ids[from]];
                var b = at[graph.Ids[to]];
                var step = -1;
                for (var s = 0; s < resolved.Length; s++)
                {
                    var (publisher, subscriber) = resolved[s];
                    if (publisher is null || subscriber is null) { continue; }
                    if ((publisher == graph.Ids[from] && subscriber == graph.Ids[to]) || (publisher == graph.Ids[to] && subscriber == graph.Ids[from]))
                    { step = s; break; }
                }
                var lit = step >= 0;
                var pathParameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["d"] = string.Create(CultureInfo.InvariantCulture, $"M {a.X:0.###} {a.Y:0.###} L {b.X:0.###} {b.Y:0.###}"),
                    ["kind"] = kind.ToString().ToLowerInvariant(),
                    ["color"] = lit ? BrainScene.GoldTrailColor : ColorOf(kind),
                    ["weight"] = graph.Weights[e].ToString("0.##", CultureInfo.InvariantCulture),
                };
                SceneNode[] children = [];
                if (lit)
                {
                    pathParameters["step"] = step.ToString(CultureInfo.InvariantCulture);
                    pathParameters["opacity"] = BrainScene.TrailOpacity(step).ToString("0.0#", CultureInfo.InvariantCulture);
                    var (publisher, _) = resolved[step];
                    var (start, end) = publisher == graph.Ids[from] ? (a, b) : (b, a);
                    children =
                    [
                        new SceneNode("s" + edge.ToString(CultureInfo.InvariantCulture), "particles", new Transform(start.X, start.Y), null,
                            new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["count"] = "12",
                                ["speed"] = "0.6",
                                ["color"] = BrainScene.GoldTrailColor,
                                ["step"] = step.ToString(CultureInfo.InvariantCulture),
                                ["opacity"] = BrainScene.TrailOpacity(step).ToString("0.0#", CultureInfo.InvariantCulture),
                            },
                            [],
                            [new Tween("x", start.X, end.X, 900, "linear", true), new Tween("y", start.Y, end.Y, 900, "linear", true)],
                            []),
                    ];
                }
                nodes.Add(new SceneNode("e" + edge.ToString(CultureInfo.InvariantCulture), "path", new Transform(), null, pathParameters, children, [], []));
                edge++;
            }
        }
        if (navigations is { Count: > 0 })
        {
            for (var n = 0; n < navigations.Count && nodes.Count < Flame.FlameVocabulary.MaxNodes - 1; n++)
            {
                var nav = navigations[n];
                var appId = ResolveAppNode(at, nav.App);
                if (appId is null || !at.TryGetValue(appId, out var a)) { continue; }
                var b = at.TryGetValue(appId + "#" + nav.Cause, out var opPos) ? opPos : (X: a.X + 36, Y: a.Y - 24);
                var navPathParams = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["d"] = string.Create(CultureInfo.InvariantCulture, $"M {a.X:0.###} {a.Y:0.###} L {b.X:0.###} {b.Y:0.###}"),
                    ["kind"] = "navigates",
                    ["color"] = BrainScene.GoldTrailColor,
                    ["cause"] = nav.Cause,
                    ["fromView"] = nav.FromView ?? string.Empty,
                    ["toView"] = nav.ToView,
                    ["step"] = n.ToString(CultureInfo.InvariantCulture),
                    ["opacity"] = BrainScene.TrailOpacity(n).ToString("0.0#", CultureInfo.InvariantCulture),
                };
                SceneNode[] navChildren =
                [
                    new SceneNode("navs" + n.ToString(CultureInfo.InvariantCulture), "particles", new Transform(a.X, a.Y), null,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["count"] = "8",
                            ["speed"] = "0.7",
                            ["color"] = BrainScene.GoldTrailColor,
                            ["step"] = n.ToString(CultureInfo.InvariantCulture),
                            ["opacity"] = BrainScene.TrailOpacity(n).ToString("0.0#", CultureInfo.InvariantCulture),
                        },
                        [],
                        [new Tween("x", a.X, b.X, 750, "linear", true), new Tween("y", a.Y, b.Y, 750, "linear", true)],
                        []),
                ];
                nodes.Add(new SceneNode("nav" + n.ToString(CultureInfo.InvariantCulture), "path", new Transform(), null, navPathParams, navChildren, [], []));
            }
        }
        return new SceneDefinition([.. nodes], new SceneCamera(Zoom: zoom <= 0 ? 1 : zoom));
    }

    private static HashSet<int> FindPulsingNodes(GraphSnapshot graph, int count)
    {
        var pulsing = new HashSet<int>();
        for (var from = 0; from < count; from++)
        {
            if (graph.Kinds[from] != GraphNodeKind.Run) { continue; }
            for (var e = graph.RowStart[from]; e < graph.RowStart[from + 1]; e++)
            {
                if (graph.EdgeKinds[e] == GraphEdgeKind.WorksOn)
                {
                    pulsing.Add(from);
                    if (graph.Col[e] >= 0 && graph.Col[e] < count)
                    {
                        pulsing.Add(graph.Col[e]);
                    }
                }
            }
        }
        return pulsing;
    }

    private static string? ResolveAppNode(Dictionary<string, (double X, double Y)> at, string app)
    {
        if (string.IsNullOrWhiteSpace(app)) { return null; }
        if (at.ContainsKey(app)) { return app; }
        var match = at.Keys.FirstOrDefault(k => k.EndsWith("/apps/" + app, StringComparison.Ordinal) || k.EndsWith("/" + app, StringComparison.Ordinal));
        return match ?? (at.ContainsKey("group:apps") ? "group:apps" : null);
    }

    // Groups sit on a ring; members of a group sit on a small circle around their group; ungrouped nodes (contracts, signals) on an outer ring.
    private static (double X, double Y)[] Layout(GraphSnapshot graph, int count)
    {
        var positions = new (double X, double Y)[count];
        var groups = graph.Groups.Length;
        var members = new Dictionary<int, List<int>>();
        var loose = new List<int>();
        for (var i = 0; i < count; i++)
        {
            if (graph.Kinds[i] == GraphNodeKind.Brain) { continue; }
            var g = graph.Group[i];
            if (g < 0 || g >= groups) { loose.Add(i); continue; }
            if (!members.TryGetValue(g, out var list)) { list = []; members[g] = list; }
            list.Add(i);
        }
        foreach (var (g, list) in members)
        {
            var centre = groups <= 1 ? (X: 0.0, Y: 0.0) : OnCircle(g, groups, GroupRadius);
            if (list.Count == 1) { positions[list[0]] = centre; continue; }
            var radius = Math.Min(110, 24 + (6 * list.Count));
            for (var k = 0; k < list.Count; k++)
            {
                var (dx, dy) = OnCircle(k, list.Count, radius);
                positions[list[k]] = (centre.X + dx, centre.Y + dy);
            }
        }
        for (var k = 0; k < loose.Count; k++) { positions[loose[k]] = OnCircle(k, loose.Count, OuterRadius); }
        return positions;
    }

    private static (double X, double Y) OnCircle(int index, int count, double radius)
    {
        if (count <= 1) { return (0, 0); }
        var angle = (Math.PI * 2 * index / count) - (Math.PI / 2);
        return (Math.Cos(angle) * radius, Math.Sin(angle) * radius);
    }

    private static string? Drill(GraphLevel level, string? focus, string id, GraphNodeKind kind, GraphGroup? group)
        => level switch
        {
            GraphLevel.Brain when id.StartsWith("group:", StringComparison.Ordinal) => "cluster:" + id["group:".Length..],
            GraphLevel.Cluster when group is { Cycle: true } => null,
            GraphLevel.Cluster when kind is GraphNodeKind.Neuron or GraphNodeKind.App or GraphNodeKind.Connection or GraphNodeKind.Task or GraphNodeKind.Step or GraphNodeKind.Run => "neurons:" + id,
            GraphLevel.Neurons when id == focus => DrillUp,
            _ => null,
        };

    // A delivery names neuron ids. At the brain level a neuron is shown by its group; at the other levels by itself.
    private static string? Resolve(GraphSnapshot graph, Dictionary<string, (double X, double Y)> at, string neuronId)
    {
        if (at.ContainsKey(neuronId)) { return neuronId; }
        if (!NeuronId.TryParse(neuronId, out var id)) { return null; }
        var dot = id.Type.IndexOf('.', StringComparison.Ordinal);
        var groupId = "group:" + (dot > 0 ? id.Type[..dot] : id.Type);
        return at.ContainsKey(groupId) && graph.Groups.Length > 0 ? groupId : null;
    }

    private static string Describe(GraphSnapshot graph, int i, GraphGroup? group)
    {
        var degree = graph.RowStart[i + 1] - graph.RowStart[i];
        var cycle = group is { Cycle: true } ? " · in a cycle" : "";
        return string.Create(CultureInfo.InvariantCulture, $"{graph.Kinds[i]} · {degree} out-edge(s){cycle}");
    }

    private static string IconOf(GraphNodeKind kind) => kind switch
    {
        GraphNodeKind.App => "apps",
        GraphNodeKind.Operation => "play_arrow",
        GraphNodeKind.Reaction => "bolt",
        GraphNodeKind.Contract => "description",
        GraphNodeKind.Signal => "sensors",
        GraphNodeKind.Connection => "link",
        GraphNodeKind.Fact or GraphNodeKind.Entity => "psychology",
        GraphNodeKind.External => "public",
        GraphNodeKind.Task => "task_alt",
        GraphNodeKind.Step => "play_circle",
        GraphNodeKind.Run => "autorenew",
        _ => "hub",
    };

    private static string ColorOf(GraphEdgeKind kind) => kind switch
    {
        GraphEdgeKind.Subscribes => "#5EEAD4",
        GraphEdgeKind.Publishes => "#38BDF8",
        GraphEdgeKind.Calls => "#F97316",
        GraphEdgeKind.Grants => "#A3E635",
        GraphEdgeKind.Relates or GraphEdgeKind.Mentions => "#A78BFA",
        GraphEdgeKind.Implements => "#22D3EE",
        GraphEdgeKind.Subtask => "#38BDF8",
        GraphEdgeKind.Blocks => "#EF4444",
        GraphEdgeKind.WorksOn => "#F59E0B",
        GraphEdgeKind.Executes => "#818CF8",
        GraphEdgeKind.Produced => "#10B981",
        _ => "#475569",
    };

    public static (GraphLevel Level, string? Focus) Next(string drill, GraphLevel level, string? focus)
    {
        ArgumentNullException.ThrowIfNull(drill);
        if (drill == DrillUp) { return (GraphLevel.Brain, null); }
        if (drill.StartsWith("cluster:", StringComparison.Ordinal)) { return (GraphLevel.Cluster, drill["cluster:".Length..]); }
        if (drill.StartsWith("neurons:", StringComparison.Ordinal)) { return (GraphLevel.Neurons, drill["neurons:".Length..]); }
        return (level, focus);
    }
}
