using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI.Memory;

namespace IntoChat.OS.Modules.AI.Tests.Unit;

public sealed class KnowledgeStoreFacts
{
    private static readonly BrainId TestBrain = new("knowledge-unit");

    private static (TestOS Os, IKnowledgeStore Store) CreateHarness(string key)
    {
        var os = TestOS.Local();
        os.Register<KnowledgeStoreNeuron>();
        var store = os.Get<IKnowledgeStore>(new NeuronId(TestBrain, "ai.knowledge-store", key));
        return (os, store);
    }

    private static IDisposable AsPerson()
        => IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("tester", "tester", TestBrain));

    [Fact]
    public async Task StoreAndGetNodePreservesProperties()
    {
        using var person = AsPerson();
        var ct = TestContext.Current.CancellationToken;
        var (os, store) = CreateHarness("props");
        await using (os)
        {
            var node = new KnowledgeNode(
                "node-1",
                "Order Processing Service",
                "microservice",
                new Dictionary<string, string> { ["language"] = "csharp", ["tier"] = "backend" },
                Importance: 8.5);

            await store.RecordNodeAsync(node, ct);
            var retrieved = await store.GetAsync("node-1", ct);

            Assert.NotNull(retrieved);
            Assert.Equal("node-1", retrieved.Id);
            Assert.Equal("Order Processing Service", retrieved.Label);
            Assert.Equal("microservice", retrieved.Type);
            Assert.Equal("csharp", retrieved.Properties["language"]);
            Assert.Equal(8.5, retrieved.Importance);
        }
    }

    [Fact]
    public async Task DeleteNodeCascadesEdgeRemoval()
    {
        using var person = AsPerson();
        var ct = TestContext.Current.CancellationToken;
        var (os, store) = CreateHarness("cascade");
        await using (os)
        {
            var nodeA = new KnowledgeNode("node-a", "Node A", "component", new Dictionary<string, string>());
            var nodeB = new KnowledgeNode("node-b", "Node B", "component", new Dictionary<string, string>());
            var edge = new KnowledgeEdge("node-a", "node-b", "depends_on", 1.0);

            await store.RecordNodeAsync(nodeA, ct);
            await store.RecordNodeAsync(nodeB, ct);
            await store.RecordEdgeAsync(edge, ct);

            var beforeEdges = await store.QueryEdgesAsync("node-a", EdgeDirection.Both, ct);
            Assert.Single(beforeEdges);

            await store.DeleteNodeAsync("node-a", ct);

            Assert.Null(await store.GetAsync("node-a", ct));
            var afterEdges = await store.QueryEdgesAsync("node-b", EdgeDirection.Both, ct);
            Assert.Empty(afterEdges);
        }
    }

    [Fact]
    public async Task GetGraphViewPerformsBfsTraversal()
    {
        using var person = AsPerson();
        var ct = TestContext.Current.CancellationToken;
        var (os, store) = CreateHarness("bfs");
        await using (os)
        {
            var root = new KnowledgeNode("root", "Root System", "root", new Dictionary<string, string>());
            var child1 = new KnowledgeNode("child-1", "Child One", "service", new Dictionary<string, string>());
            var child2 = new KnowledgeNode("child-2", "Child Two", "service", new Dictionary<string, string>());
            var leaf = new KnowledgeNode("leaf", "Leaf Worker", "worker", new Dictionary<string, string>());

            await store.RecordNodeAsync(root, ct);
            await store.RecordNodeAsync(child1, ct);
            await store.RecordNodeAsync(child2, ct);
            await store.RecordNodeAsync(leaf, ct);

            await store.RecordEdgeAsync(new KnowledgeEdge("root", "child-1", "parent_of"), ct);
            await store.RecordEdgeAsync(new KnowledgeEdge("root", "child-2", "parent_of"), ct);
            await store.RecordEdgeAsync(new KnowledgeEdge("child-1", "leaf", "dispatches_to"), ct);

            var viewDepth1 = await store.GetGraphViewAsync("root", depth: 1, ct: ct);
            Assert.Equal(3, viewDepth1.Nodes.Count);
            Assert.DoesNotContain(viewDepth1.Nodes, n => n.Id == "leaf");

            var viewDepth2 = await store.GetGraphViewAsync("root", depth: 2, ct: ct);
            Assert.Equal(4, viewDepth2.Nodes.Count);
            Assert.Contains(viewDepth2.Nodes, n => n.Id == "leaf");
        }
    }

    [Fact]
    public async Task SemanticSearchPropagatesScoreViaSpreadingActivation()
    {
        using var person = AsPerson();
        var ct = TestContext.Current.CancellationToken;
        var (os, store) = CreateHarness("spread");
        await using (os)
        {
            var authNode = new KnowledgeNode("auth", "Authentication Gateway", "security", new Dictionary<string, string> { ["protocol"] = "oauth" }, Importance: 5.0);
            var tokenNode = new KnowledgeNode("token", "Token Issuer", "crypto", new Dictionary<string, string> { ["key"] = "rsa" }, Importance: 5.0);
            var unlinkedNode = new KnowledgeNode("printer", "Network Printer", "hardware", new Dictionary<string, string>(), Importance: 1.0);

            await store.RecordNodeAsync(authNode, ct);
            await store.RecordNodeAsync(tokenNode, ct);
            await store.RecordNodeAsync(unlinkedNode, ct);

            await store.RecordEdgeAsync(new KnowledgeEdge("auth", "token", "issues_for", Weight: 2.0), ct);

            var hits = await store.SemanticSearchAsync("Authentication", limit: 3, ct: ct);

            Assert.NotEmpty(hits);
            Assert.Equal("auth", hits[0].Node.Id);

            var tokenHit = Assert.Single(hits, h => h.Node.Id == "token");
            Assert.True(tokenHit.RelevanceScore > 0);
        }
    }
}
