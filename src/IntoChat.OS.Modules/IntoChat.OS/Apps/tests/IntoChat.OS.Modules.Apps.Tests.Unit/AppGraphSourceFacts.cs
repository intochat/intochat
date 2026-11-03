using DigitalBrain;
using DigitalBrain.Graph;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class AppGraphSourceFacts
{
    private static AppSnapshot Snapshot(params string[] operations) => new(
        "notes", new AppManifest("Notes", "Notes", [.. operations.Select(name => new AppOperation(name, name))], [], []),
        "", [], [], [], "1.0.0", AppStatus.Live, [], []);

    [Fact]
    public void EachManifestOperationIsANodeTheAppOwns()
    {
        var brain = new BrainId("ada");
        var sink = new GraphSink();
        sink.Node("ada", GraphNodeKind.Brain, "ada");
        var app = sink.Node("ada/apps/notes", GraphNodeKind.App, "apps/notes");

        AppGraphSource.Contribute(brain, sink, [("notes", Snapshot("open", "add")), ("gone", Snapshot("open"))]);

        var graph = GraphCompute.Build(sink, brain);
        Assert.Equal(4, graph.NodeCount);
        var open = graph.IndexOf("ada/apps/notes#open");
        Assert.True(open >= 0);
        Assert.Equal(GraphNodeKind.Operation, graph.Kinds[open]);
        Assert.Equal(graph.Group[app], graph.Group[open]);
        Assert.Equal("apps", graph.Groups[graph.Group[open]].Label);
        Assert.Equal(2, graph.RowStart[app + 1] - graph.RowStart[app]);
        Assert.All(Enumerable.Range(graph.RowStart[app], 2), e => Assert.Equal(GraphEdgeKind.Owns, graph.EdgeKinds[e]));
        Assert.Equal(-1, graph.IndexOf("ada/apps/gone#open"));
    }

    /* Watcher test app that subscribes to settings and watches profile changes so AppGraphSource can emit Subscribes edges. */
    [DigitalBrain.App("watcher")]
    private sealed class WatcherApp : DigitalBrain.App
    {
        [DigitalBrain.On("settings", Contract = typeof(IUserSettings))]
        public void OnSettings(UserSettingsChanged? changed) => _ = changed;

        protected override void Watches(WatchBuilder watch)
            => watch.On<IProfile, ProfileChanged>("profile", _ => "profile", _ => Task.CompletedTask);
    }

    [Fact]
    public void InProcessSubscriptionsAndWatchesEmitSubscribesEdgesFromTheApp()
    {
        var brain = new BrainId("ada");
        var sink = new GraphSink();
        sink.Node("ada", GraphNodeKind.Brain, "ada");
        var appKey = "ada/apps/watcher";
        var app = sink.Node(appKey, GraphNodeKind.App, "apps/watcher");
        var def = new AppDefinition(typeof(WatcherApp));
        var instance = new WatcherApp();

        AppGraphSource.ContributeSubscriptions(brain, sink, appKey, def.SignalSubscriptions, instance.DeclaredWatches());

        var graph = GraphCompute.Build(sink, brain);
        var settingsIdx = graph.IndexOf("ada/os.settings/settings");
        var profileIdx = graph.IndexOf("ada/os.profile/profile");
        Assert.True(settingsIdx >= 0);
        Assert.True(profileIdx >= 0);
        var appEdges = Enumerable.Range(graph.RowStart[app], graph.RowStart[app + 1] - graph.RowStart[app]).ToArray();
        Assert.Contains(appEdges, e => graph.Col[e] == settingsIdx && graph.EdgeKinds[e] == GraphEdgeKind.Subscribes);
        Assert.Contains(appEdges, e => graph.Col[e] == profileIdx && graph.EdgeKinds[e] == GraphEdgeKind.Subscribes);
    }
}
