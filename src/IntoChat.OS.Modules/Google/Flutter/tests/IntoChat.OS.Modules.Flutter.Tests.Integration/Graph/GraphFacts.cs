using IntoChat.OS.Modules.Flutter.Graph;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Graph;

public sealed class GraphFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task RenderWritesTitleNodesAndEdges()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .StartAsync(ct);
        await brain.Get<IGraph>("net").Render("N", [new GraphNode("a", "A")], [new GraphEdge("a", "a")]);
        var state = await brain.Get<IGraph>("net").Read();
        Assert.Equal("N", state.Title);
        Assert.Equal(new GraphNode("a", "A"), Assert.Single(state.Nodes));
        Assert.Equal(new GraphEdge("a", "a"), Assert.Single(state.Edges));
    }
}
