using DigitalBrain;
using DigitalBrain.Graph;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class BrainSceneGraphFacts
{
    // ada owns two neurons in the "probe" group and one app in the "apps" group; the app subscribes to a probe.
    private static GraphSnapshot Snapshot()
    {
        string[] ids = ["ada", "ada/probe.ticker/t", "ada/probe.follower/f", "ada/apps.app2/apps/notes", "signal:ticked"];
        GraphNodeKind[] kinds = [GraphNodeKind.Brain, GraphNodeKind.Neuron, GraphNodeKind.Neuron, GraphNodeKind.App, GraphNodeKind.Signal];
        string[] labels = ["ada", "probe.ticker/t", "probe.follower/f", "apps.app2/apps/notes", "ticked"];
        int[] group = [-1, 0, 0, 1, -1];
        // edges by source: 0→1 owns, 0→2 owns, 0→3 owns, 1→4 publishes, 2→1 subscribes, 3→1 subscribes
        int[] rowStart = [0, 3, 4, 5, 6, 6];
        int[] col = [1, 2, 3, 4, 1, 1];
        GraphEdgeKind[] edgeKinds = [GraphEdgeKind.Owns, GraphEdgeKind.Owns, GraphEdgeKind.Owns, GraphEdgeKind.Publishes, GraphEdgeKind.Subscribes, GraphEdgeKind.Subscribes];
        float[] weights = [1, 1, 1, 1, 1, 1];
        GraphGroup[] groups = [new("probe", "probe", GraphNodeKind.Neuron, 2, false), new("apps", "apps", GraphNodeKind.Neuron, 1, false)];
        return new GraphSnapshot(1, ids, kinds, labels, group, rowStart, col, edgeKinds, weights, groups);
    }

    [Fact]
    public void TheBrainNodeIsNotDrawnAndEdgesCarryTheirKind()
    {
        var scene = BrainSceneGraph.Compose(Snapshot(), GraphLevel.Neurons, null, [], 1);

        var spheres = scene.Nodes.Where(node => node.Type == "sphere").ToArray();
        Assert.Equal(4, spheres.Length);
        Assert.DoesNotContain(spheres, node => node.Parameters["graphId"] == "ada");
        var paths = scene.Nodes.Where(node => node.Type == "path").ToArray();
        Assert.Equal(3, paths.Length);
        Assert.Contains(paths, path => path.Parameters["kind"] == "subscribes");
        Assert.Contains(paths, path => path.Parameters["kind"] == "publishes");
        Assert.All(spheres, node => Assert.Contains("tap", node.Inputs));
    }

    [Fact]
    public void MembersOfOneGroupSitTogetherAndApartFromTheOtherGroup()
    {
        var scene = BrainSceneGraph.Compose(Snapshot(), GraphLevel.Neurons, null, [], 1);
        var byId = scene.Nodes.Where(node => node.Type == "sphere").ToDictionary(node => node.Parameters["graphId"], node => node.Transform);

        static double Distance(IntoChat.OS.Modules.Flame.Scene.Transform a, IntoChat.OS.Modules.Flame.Scene.Transform b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        var within = Distance(byId["ada/probe.ticker/t"], byId["ada/probe.follower/f"]);
        var across = Distance(byId["ada/probe.ticker/t"], byId["ada/apps.app2/apps/notes"]);
        Assert.True(within < across, $"{within} should be under {across}");
    }

    [Fact]
    public void ADeliveryLightsTheMatchingEdgeInGold()
    {
        var scene = BrainSceneGraph.Compose(Snapshot(), GraphLevel.Neurons, null, [new SignalDelivery("ada/probe.ticker/t", "ada/probe.follower/f", "ticked", 9)], 1);

        var lit = Assert.Single(scene.Nodes, node => node.Type == "path" && node.Children.Length == 1);
        Assert.Equal(BrainScene.GoldTrailColor, lit.Parameters["color"]);
        Assert.Equal("0", lit.Parameters["step"]);
        var spark = lit.Children[0];
        Assert.Equal("particles", spark.Type);
        Assert.Contains(spark.Tweens, tween => tween.Property == "x" && tween.Repeat);
    }

    [Fact]
    public void DrillParametersGoDownAndBackUp()
    {
        var brainLevel = new GraphSnapshot(1, ["group:probe", "group:apps"], [GraphNodeKind.Neuron, GraphNodeKind.Neuron], ["probe", "apps"], [0, 1],
            [0, 0, 1], [0], [GraphEdgeKind.Subscribes], [2f], [new("probe", "probe", GraphNodeKind.Neuron, 2, false), new("apps", "apps", GraphNodeKind.Neuron, 1, false)]);
        var top = BrainSceneGraph.Compose(brainLevel, GraphLevel.Brain, null, [], 1);
        Assert.Equal("cluster:probe", top.Nodes[0].Parameters[BrainSceneGraph.DrillParameter]);
        Assert.Equal((GraphLevel.Cluster, "probe"), BrainSceneGraph.Next("cluster:probe", GraphLevel.Brain, null));

        var cluster = BrainSceneGraph.Compose(Snapshot(), GraphLevel.Cluster, "probe", [], 1);
        var ticker = cluster.Nodes.Single(node => node.Type == "sphere" && node.Parameters["graphId"] == "ada/probe.ticker/t");
        Assert.Equal("neurons:ada/probe.ticker/t", ticker.Parameters[BrainSceneGraph.DrillParameter]);
        Assert.Equal((GraphLevel.Neurons, "ada/probe.ticker/t"), BrainSceneGraph.Next(ticker.Parameters[BrainSceneGraph.DrillParameter], GraphLevel.Cluster, "probe"));

        var neurons = BrainSceneGraph.Compose(Snapshot(), GraphLevel.Neurons, "ada/probe.ticker/t", [], 1);
        var focused = neurons.Nodes.Single(node => node.Type == "sphere" && node.Parameters["graphId"] == "ada/probe.ticker/t");
        Assert.Equal(BrainSceneGraph.DrillUp, focused.Parameters[BrainSceneGraph.DrillParameter]);
        Assert.Equal((GraphLevel.Brain, null), BrainSceneGraph.Next(BrainSceneGraph.DrillUp, GraphLevel.Neurons, "ada/probe.ticker/t"));
    }

    [Fact]
    public void AtTheBrainLevelADeliveryLightsTheEdgeBetweenGroups()
    {
        var brainLevel = new GraphSnapshot(1, ["group:probe", "group:apps"], [GraphNodeKind.Neuron, GraphNodeKind.Neuron], ["probe", "apps"], [0, 1],
            [0, 0, 1], [0], [GraphEdgeKind.Subscribes], [2f], [new("probe", "probe", GraphNodeKind.Neuron, 2, false), new("apps", "apps", GraphNodeKind.Neuron, 1, false)]);
        var scene = BrainSceneGraph.Compose(brainLevel, GraphLevel.Brain, null, [new SignalDelivery("ada/probe.ticker/t", "ada/apps.app2/apps/notes", "ticked", 1)], 1);

        var path = Assert.Single(scene.Nodes, node => node.Type == "path");
        Assert.Single(path.Children);
        Assert.Equal("2", path.Parameters["weight"]);
    }
}
