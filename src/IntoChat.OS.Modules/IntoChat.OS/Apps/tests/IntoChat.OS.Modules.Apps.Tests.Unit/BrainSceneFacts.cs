using DigitalBrain;
using DigitalBrain.Spi;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class BrainSceneFacts
{
    [Fact]
    public void NeuronsAreSpheresAndSynapsesArePaths()
    {
        var ada = new BrainInstance("apps.app2", "ada/apps.app2/apps/notes", DateTimeOffset.UnixEpoch, null);
        var files = new BrainInstance("apps.files", "ada/apps.files/apps/files/workspace", DateTimeOffset.UnixEpoch, null);
        var scene = BrainScene.Compose(
            [files, ada],
            [new ObservedSynapse(ada.Key, files.Key, "app.requested")],
            null,
            1);
        Assert.Equal(3, scene.Nodes.Length);
        Assert.Equal(["n0", "n1", "e0"], scene.Nodes.Select(node => node.Id).ToArray());
        Assert.All(scene.Nodes.Take(2), node => Assert.Equal("sphere", node.Type));
        Assert.Equal("hub", scene.Nodes[0].Parameters["icon"]);
        Assert.Equal("apps.app2", scene.Nodes[0].Parameters["neuronType"]);
        Assert.Equal("ada/apps.app2/apps/notes", scene.Nodes[0].Parameters["neuronKey"]);
        Assert.Equal("ada/apps.app2/apps/notes", scene.Nodes[0].Parameters["neuronId"]);
        Assert.Contains("tap", scene.Nodes[0].Inputs);
        Assert.Equal("path", scene.Nodes[2].Type);
        Assert.StartsWith("M ", scene.Nodes[2].Parameters["d"], StringComparison.Ordinal);
        Assert.Empty(scene.Nodes[2].Children);
        var apart = Math.Abs(scene.Nodes[0].Transform.Y - scene.Nodes[1].Transform.Y);
        Assert.True(apart > 100);
    }

    [Fact]
    public void ADeliveryPutsParticlesOnTheMatchingPath()
    {
        var from = new BrainInstance("a", "ada/a/left", DateTimeOffset.UnixEpoch, null);
        var to = new BrainInstance("b", "ada/b/right", DateTimeOffset.UnixEpoch, null);
        var other = new ObservedSynapse(to.Key, from.Key, "other");
        var live = new ObservedSynapse(from.Key, to.Key, "hit");
        var scene = BrainScene.Compose(
            [from, to],
            [other, live],
            new SignalDelivery(live.From, live.To, live.Signal, 7),
            1);
        var paths = scene.Nodes.Where(node => node.Type == "path").ToArray();
        Assert.Equal(2, paths.Length);
        var spark = Assert.Single(paths.SelectMany(path => path.Children));
        Assert.Equal("particles", spark.Type);
        Assert.Equal("12", spark.Parameters["count"]);
        Assert.Equal("#5EEAD4", spark.Parameters["color"]);
        Assert.Contains(spark.Tweens, tween => tween.Property == "x" && tween.Repeat);
        Assert.Contains(spark.Tweens, tween => tween.Property == "y" && tween.Repeat);
    }

    [Fact]
    public void ADirectoryKeyMatchesTheNeuronIdASynapseStores()
    {
        var calculator = new BrainInstance("apps.app2", "owner/apps/calculator", DateTimeOffset.UnixEpoch, null);
        var button = new BrainInstance("flutter.button", "owner/apps/calculator/view/key-0", DateTimeOffset.UnixEpoch, null);
        var synapse = new ObservedSynapse("owner/apps.app2/apps/calculator", "owner/flutter.button/apps/calculator/view/key-0", "ui.clicked");
        var scene = BrainScene.Compose(
            [calculator, button],
            [synapse],
            new SignalDelivery(synapse.From, synapse.To, synapse.Signal, 3),
            1);
        var path = Assert.Single(scene.Nodes, node => node.Type == "path");
        var spark = Assert.Single(path.Children);
        Assert.Equal("particles", spark.Type);
    }

    [Fact]
    public void ASessionNeuronMissingFromTheDirectoryStillCarriesTheParticle()
    {
        var view = new BrainInstance("view", "owner/apps/calculator/view", DateTimeOffset.UnixEpoch, null);
        var synapse = new ObservedSynapse("owner/view/apps/calculator/view", "owner/runtime.session/calc", "ui.view-action");
        var scene = BrainScene.Compose(
            [view],
            [synapse],
            new SignalDelivery(synapse.From, synapse.To, synapse.Signal, 4),
            1);
        Assert.Equal(2, scene.Nodes.Count(node => node.Type == "sphere"));
        var path = Assert.Single(scene.Nodes, node => node.Type == "path");
        Assert.Equal("particles", Assert.Single(path.Children).Type);
    }

    [Fact]
    public void ATapZoomsUntilTheCap()
    {
        Assert.Equal(1.5, BrainScene.NextZoom(1));
        Assert.Equal(BrainScene.ZoomCap, BrainScene.NextZoom(3));
        Assert.Equal(BrainScene.ZoomCap, BrainScene.NextZoom(BrainScene.ZoomCap));
    }

    [Fact]
    public void TenStepGoldTrailFadesByTenPercentPerStepAndCapsAtTen()
    {
        Assert.Equal(1.0, BrainScene.TrailOpacity(0));
        Assert.Equal(0.9, BrainScene.TrailOpacity(1));
        Assert.Equal(0.5, BrainScene.TrailOpacity(5));
        Assert.Equal(0.1, BrainScene.TrailOpacity(9));
        Assert.Equal(0.0, BrainScene.TrailOpacity(10));
        Assert.Equal(0.0, BrainScene.TrailOpacity(-1));

        var neurons = Enumerable.Range(0, 12)
            .Select(i => new BrainInstance("n", $"ada/n/node-{i}", DateTimeOffset.UnixEpoch, null))
            .ToArray();
        var synapses = Enumerable.Range(0, 11)
            .Select(i => new ObservedSynapse(neurons[i].Key, neurons[i + 1].Key, $"sig-{i}"))
            .ToArray();
        var trail = synapses
            .Select((s, idx) => new SignalDelivery(s.From, s.To, s.Signal, idx + 1))
            .ToArray();

        var scene = BrainScene.ComposeTrail(neurons, synapses, trail, 1.0);
        var litPaths = scene.Nodes
            .Where(n => n.Type == "path" && n.Children.Length > 0)
            .ToArray();

        Assert.Equal(BrainScene.MaxTrailSteps, litPaths.Length);
        var newest = litPaths.Single(p => p.Parameters["step"] == "0");
        Assert.Equal(BrainScene.GoldTrailColor, newest.Parameters["color"]);
        Assert.Equal("1.0", newest.Parameters["opacity"]);

        var oldest = litPaths.Single(p => p.Parameters["step"] == "9");
        Assert.Equal(BrainScene.GoldTrailColor, oldest.Parameters["color"]);
        Assert.Equal("0.1", oldest.Parameters["opacity"]);
    }
}
