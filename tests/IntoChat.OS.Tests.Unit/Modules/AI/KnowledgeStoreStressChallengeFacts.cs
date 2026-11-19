using System.Reflection;
using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI.Memory;

namespace IntoChat.OS.Modules.AI.Tests.Unit;

public sealed class KnowledgeStoreStressChallengeFacts
{
    private static readonly BrainId StressBrain = new("knowledge-stress");

    private static (TestOS os, IKnowledgeStore store) CreateNeuronHarness(string id = "test-store")
    {
        var os = TestOS.Local();
        os.Register<KnowledgeStoreNeuron>();
        var store = os.Get<IKnowledgeStore>(new NeuronId(StressBrain, "ai.knowledge-store", id));
        return (os, store);
    }

    private static IDisposable AsPerson()
        => IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("stress", "stress", StressBrain));

    [Fact]
    public async Task StoreAsync_OverloadAmbiguity_ResolvesCleanlyOnNeuronProxy()
    {
        using var person = AsPerson();
        var ct = TestContext.Current.CancellationToken;
        var (os, store) = CreateNeuronHarness("overload-test");
        await using (os)
        {
            var node = new KnowledgeNode("ambiguous-node", "Ambiguous", "concept", new Dictionary<string, string>());
            var edge = new KnowledgeEdge("ambiguous-node", "ambiguous-node", "self");

            await store.RecordNodeAsync(node, ct);
            await store.RecordEdgeAsync(edge, ct);

            var retrieved = await store.GetAsync("ambiguous-node", ct);
            Assert.NotNull(retrieved);
            Assert.Equal("ambiguous-node", retrieved.Id);

            var edges = await store.QueryEdgesAsync("ambiguous-node", EdgeDirection.Both, ct);
            Assert.Single(edges);
        }
    }

    [Fact]
    public async Task CyclicGraph_SelfLoop_TerminatesWithoutDuplicationOrLoop()
    {
        using var person = AsPerson();
        var ct = TestContext.Current.CancellationToken;
        var (os, store) = CreateNeuronHarness("cyclic-self");
        await using (os)
        {
            var nodeA = new KnowledgeNode("node-a", "Node A", "concept", new Dictionary<string, string>());
            await store.RecordNodeAsync(nodeA, ct);
            await store.RecordEdgeAsync(new KnowledgeEdge("node-a", "node-a", "self_referential"), ct);

            foreach (var depth in new[] { 1, 2, 5, 10 })
            {
                var view = await store.GetGraphViewAsync("node-a", depth, ct);
                Assert.NotNull(view);
                Assert.Single(view.Nodes);
                Assert.Equal("node-a", view.Nodes[0].Id);
                Assert.Single(view.Edges);
                Assert.Equal("node-a", view.Edges[0].SourceId);
                Assert.Equal("node-a", view.Edges[0].TargetId);
            }
        }
    }

    [Fact]
    public async Task CyclicGraph_MutualCycle_TerminatesWithExactNodesAndEdges()
    {
        using var person = AsPerson();
        var ct = TestContext.Current.CancellationToken;
        var (os, store) = CreateNeuronHarness("cyclic-mutual");
        await using (os)
        {
            var nodeA = new KnowledgeNode("node-a", "Node A", "entity", new Dictionary<string, string>());
            var nodeB = new KnowledgeNode("node-b", "Node B", "entity", new Dictionary<string, string>());
            await store.RecordNodeAsync(nodeA, ct);
            await store.RecordNodeAsync(nodeB, ct);
            await store.RecordEdgeAsync(new KnowledgeEdge("node-a", "node-b", "calls"), ct);
            await store.RecordEdgeAsync(new KnowledgeEdge("node-b", "node-a", "replies"), ct);

            var view = await store.GetGraphViewAsync("node-a", depth: 5, ct);
            Assert.NotNull(view);
            Assert.Equal(2, view.Nodes.Count);
            Assert.Contains(view.Nodes, n => n.Id == "node-a");
            Assert.Contains(view.Nodes, n => n.Id == "node-b");

            Assert.Equal(2, view.Edges.Count);
            Assert.Contains(view.Edges, e => e.SourceId == "node-a" && e.TargetId == "node-b" && e.Relation == "calls");
            Assert.Contains(view.Edges, e => e.SourceId == "node-b" && e.TargetId == "node-a" && e.Relation == "replies");
        }
    }

    [Fact]
    public async Task CyclicGraph_TriangleAndDenseMesh_DoesNotCauseStackOverflowOrInfiniteLoop()
    {
        using var person = AsPerson();
        var ct = TestContext.Current.CancellationToken;
        var (os, store) = CreateNeuronHarness("cyclic-mesh");
        await using (os)
        {
            const int nodeCount = 10;
            for (var i = 0; i < nodeCount; i++)
            {
                await store.RecordNodeAsync(new KnowledgeNode($"mesh-{i}", $"Mesh Node {i}", "node", new Dictionary<string, string>()), ct);
            }

            for (var i = 0; i < nodeCount; i++)
            {
                for (var j = 0; j < nodeCount; j++)
                {
                    if (i != j)
                    {
                        await store.RecordEdgeAsync(new KnowledgeEdge($"mesh-{i}", $"mesh-{j}", "connected_to"), ct);
                    }
                }
            }

            var view = await store.GetGraphViewAsync("mesh-0", depth: 5, ct);
            Assert.NotNull(view);
            Assert.Equal(nodeCount, view.Nodes.Count);
            Assert.Equal(90, view.Edges.Count);
        }
    }

    [Fact]
    public async Task CascadingDeletion_RemovesAllIncidentEdgesBothWays()
    {
        using var person = AsPerson();
        var ct = TestContext.Current.CancellationToken;
        var (os, store) = CreateNeuronHarness("cascade-del");
        await using (os)
        {
            var center = new KnowledgeNode("center", "Center Hub", "hub", new Dictionary<string, string>());
            await store.RecordNodeAsync(center, ct);
            await store.RecordEdgeAsync(new KnowledgeEdge("center", "center", "loop"), ct);

            for (var i = 0; i < 5; i++)
            {
                await store.RecordNodeAsync(new KnowledgeNode($"sat-{i}", $"Satellite {i}", "satellite", new Dictionary<string, string>()), ct);
                await store.RecordEdgeAsync(new KnowledgeEdge("center", $"sat-{i}", "controls"), ct);
                await store.RecordEdgeAsync(new KnowledgeEdge($"sat-{i}", "center", "reports_to"), ct);
            }

            var beforeEdges = await store.QueryEdgesAsync("center", EdgeDirection.Both, ct);
            Assert.Equal(11, beforeEdges.Count);

            await store.DeleteNodeAsync("center", ct);
            Assert.Null(await store.GetAsync("center", ct));

            for (var i = 0; i < 5; i++)
            {
                Assert.Empty(await store.QueryEdgesAsync($"sat-{i}", EdgeDirection.Both, ct));
            }

            var view = await store.GetGraphViewAsync(null, depth: 2, ct);
            Assert.Equal(5, view.Nodes.Count);
            Assert.Empty(view.Edges);
        }
    }

    [Fact]
    public async Task BoundaryConditions_EmptyStoreAndDisconnectedComponents()
    {
        using var person = AsPerson();
        var ct = TestContext.Current.CancellationToken;
        var (os, store) = CreateNeuronHarness("boundary");
        await using (os)
        {
            var emptyView = await store.GetGraphViewAsync("non-existent", depth: 2, ct);
            Assert.Empty(emptyView.Nodes);
            Assert.Empty(emptyView.Edges);

            var nullRootView = await store.GetGraphViewAsync(null, depth: 2, ct);
            Assert.Empty(nullRootView.Nodes);
            Assert.Empty(nullRootView.Edges);

            Assert.Empty(await store.QueryEdgesAsync("non-existent", EdgeDirection.Both, ct));
            Assert.Empty(await store.SemanticSearchAsync("", limit: 10, ct));
            Assert.Empty(await store.SemanticSearchAsync("query", limit: 0, ct));
            Assert.Empty(await store.SemanticSearchAsync("query", limit: -5, ct));

            await store.DeleteNodeAsync("non-existent", ct);
            await store.DeleteEdgeAsync("source", "dst", "rel", ct);

            await store.RecordNodeAsync(new KnowledgeNode("c1-a", "Comp1 A", "type", new Dictionary<string, string>()), ct);
            await store.RecordNodeAsync(new KnowledgeNode("c1-b", "Comp1 B", "type", new Dictionary<string, string>()), ct);
            await store.RecordEdgeAsync(new KnowledgeEdge("c1-a", "c1-b", "links"), ct);

            await store.RecordNodeAsync(new KnowledgeNode("c2-x", "Comp2 X", "type", new Dictionary<string, string>()), ct);
            await store.RecordNodeAsync(new KnowledgeNode("c2-y", "Comp2 Y", "type", new Dictionary<string, string>()), ct);
            await store.RecordEdgeAsync(new KnowledgeEdge("c2-x", "c2-y", "links"), ct);

            var c1View = await store.GetGraphViewAsync("c1-a", depth: 5, ct);
            Assert.Equal(2, c1View.Nodes.Count);
            Assert.Contains(c1View.Nodes, n => n.Id == "c1-a");
            Assert.Contains(c1View.Nodes, n => n.Id == "c1-b");
            Assert.DoesNotContain(c1View.Nodes, n => n.Id.StartsWith("c2", StringComparison.Ordinal));
            Assert.Single(c1View.Edges);

            var fullView = await store.GetGraphViewAsync(null, depth: 2, ct);
            Assert.Equal(4, fullView.Nodes.Count);
            Assert.Equal(2, fullView.Edges.Count);
        }
    }

    [Fact]
    public void KnowledgeStoreState_ConformsToPersistedStateRules_PureArrays()
    {
        var stateType = typeof(KnowledgeStoreState);
        Assert.NotNull(stateType.GetCustomAttribute<GenerateSerializerAttribute>());
        Assert.NotNull(stateType.GetCustomAttribute<AliasAttribute>());

        foreach (var prop in stateType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var pType = prop.PropertyType;
            if (pType.IsClass && pType != typeof(string))
            {
                Assert.True(pType.IsArray, $"Property {prop.Name} in KnowledgeStoreState must be an array, but is {pType.Name}");
            }
        }

        foreach (var recordType in new[] { typeof(KnowledgeNodeRecord), typeof(KnowledgeEdgeRecord), typeof(KnowledgePropertyRecord) })
        {
            Assert.NotNull(recordType.GetCustomAttribute<GenerateSerializerAttribute>());
            foreach (var prop in recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var pType = prop.PropertyType;
                if (pType.IsClass && pType != typeof(string))
                {
                    Assert.True(pType.IsArray, $"Property {prop.Name} in {recordType.Name} must be an array, but is {pType.Name}");
                }
            }
        }
    }

    [Fact]
    public async Task SemanticSearch_WithCyclicMesh_AssignsFiniteScoresWithoutExploding()
    {
        using var person = AsPerson();
        var ct = TestContext.Current.CancellationToken;
        var (os, store) = CreateNeuronHarness("cyclic-search");
        await using (os)
        {
            var nodeA = new KnowledgeNode("node-a", "Quantum Encryption Protocol", "security", new Dictionary<string, string> { ["algo"] = "lattice" }, Importance: 5.0);
            var nodeB = new KnowledgeNode("node-b", "Key Distribution Center", "security", new Dictionary<string, string> { ["protocol"] = "bb84" }, Importance: 5.0);
            var nodeC = new KnowledgeNode("node-c", "Entropy Source Hardware", "crypto", new Dictionary<string, string> { ["hw"] = "trng" }, Importance: 5.0);

            await store.RecordNodeAsync(nodeA, ct);
            await store.RecordNodeAsync(nodeB, ct);
            await store.RecordNodeAsync(nodeC, ct);

            await store.RecordEdgeAsync(new KnowledgeEdge("node-a", "node-b", "routes_keys", Weight: 2.0), ct);
            await store.RecordEdgeAsync(new KnowledgeEdge("node-b", "node-a", "confirms_routes", Weight: 2.0), ct);
            await store.RecordEdgeAsync(new KnowledgeEdge("node-b", "node-c", "harvests_entropy", Weight: 2.0), ct);
            await store.RecordEdgeAsync(new KnowledgeEdge("node-c", "node-a", "seeds_lattice", Weight: 2.0), ct);

            var hits = await store.SemanticSearchAsync("Quantum Encryption", limit: 5, ct);

            Assert.NotEmpty(hits);
            Assert.Contains(hits, h => h.Node.Id == "node-a");
            Assert.Contains(hits, h => h.Node.Id == "node-b");

            foreach (var hit in hits)
            {
                Assert.False(double.IsNaN(hit.RelevanceScore));
                Assert.False(double.IsInfinity(hit.RelevanceScore));
                Assert.True(hit.RelevanceScore > 0);
            }
        }
    }
}
