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
    public void ATapZoomsUntilTheCap()
    {
        Assert.Equal(1.5, BrainScene.NextZoom(1));
        Assert.Equal(BrainScene.ZoomCap, BrainScene.NextZoom(3));
        Assert.Equal(BrainScene.ZoomCap, BrainScene.NextZoom(BrainScene.ZoomCap));
    }
}
