using DigitalBrain;
using DigitalBrain.Graph;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public async Task CompiledScreenAppsAndTheirOperationsJoinTheBrainGraphWithoutError()
    {
        var brain = new BrainId("ada");
        var sink = new GraphSink();
        var root = sink.Node("ada", GraphNodeKind.Brain, "ada");
        var services = new ServiceCollection()
            .AddSingleton(new CompiledAppsRegistration(typeof(DesktopApp).Assembly))
            .BuildServiceProvider();

        await new AppGraphSource(services).Contribute(brain, sink, TestContext.Current.CancellationToken);

        var graph = GraphCompute.Build(sink, brain);
        var desktop = graph.IndexOf("ada/apps/desktop");
        var ino = graph.IndexOf("ada/apps/ino");
        var creator = graph.IndexOf("ada/apps/creator");
        Assert.True(desktop >= 0);
        Assert.True(ino >= 0);
        Assert.True(creator >= 0);
        Assert.Equal(GraphNodeKind.App, graph.Kinds[desktop]);

        var rootEdges = Enumerable.Range(graph.RowStart[root], graph.RowStart[root + 1] - graph.RowStart[root]).ToArray();
        Assert.Contains(rootEdges, e => graph.Col[e] == desktop && graph.EdgeKinds[e] == GraphEdgeKind.Owns);

        var desktopEdges = Enumerable.Range(graph.RowStart[desktop], graph.RowStart[desktop + 1] - graph.RowStart[desktop]).ToArray();
        Assert.NotEmpty(desktopEdges);
        Assert.All(desktopEdges, e =>
        {
            Assert.Equal(GraphEdgeKind.Owns, graph.EdgeKinds[e]);
            Assert.Equal(GraphNodeKind.Operation, graph.Kinds[graph.Col[e]]);
        });
    }
}
