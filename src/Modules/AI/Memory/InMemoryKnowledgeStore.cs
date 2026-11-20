using System.Collections.Concurrent;

namespace IntoChat.OS.Modules.AI.Memory;

// Ephemeral in-memory IKnowledgeStore implementation used by non-neuron unit/scenario harnesses;
// production brain storage runs through KnowledgeStoreNeuron (not registered as a process-wide DI singleton).
internal sealed class InMemoryKnowledgeStore : IKnowledgeStore
{
    private readonly ConcurrentDictionary<string, KnowledgeNode> _nodes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, KnowledgeEdge> _edges = new(StringComparer.Ordinal);

    public Task RecordNodeAsync(KnowledgeNode node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(node.Id);
        ct.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        _nodes[node.Id] = node with
        {
            CreatedAt = node.CreatedAt == default ? now : node.CreatedAt,
            UpdatedAt = now,
        };
        return Task.CompletedTask;
    }

    public Task<KnowledgeNode?> GetAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(id)) { return Task.FromResult<KnowledgeNode?>(null); }
        _nodes.TryGetValue(id, out var node);
        return Task.FromResult(node);
    }

    public Task DeleteNodeAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(id)) { return Task.CompletedTask; }
        _nodes.TryRemove(id, out _);
        foreach (var (key, edge) in _edges)
        {
            if (string.Equals(edge.SourceId, id, StringComparison.Ordinal) || string.Equals(edge.TargetId, id, StringComparison.Ordinal))
            {
                _edges.TryRemove(key, out _);
            }
        }
        return Task.CompletedTask;
    }

    public Task RecordEdgeAsync(KnowledgeEdge edge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentException.ThrowIfNullOrWhiteSpace(edge.SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(edge.TargetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(edge.Relation);
        ct.ThrowIfCancellationRequested();

        _edges[EdgeKey(edge.SourceId, edge.TargetId, edge.Relation)] = edge with
        {
            CreatedAt = edge.CreatedAt == default ? DateTimeOffset.UtcNow : edge.CreatedAt,
        };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<KnowledgeEdge>> QueryEdgesAsync(string nodeId, EdgeDirection direction = EdgeDirection.Both, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(FilterEdges(_edges.Values, nodeId, direction));
    }

    public Task DeleteEdgeAsync(string sourceId, string targetId, string relation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(sourceId) && !string.IsNullOrWhiteSpace(targetId) && !string.IsNullOrWhiteSpace(relation))
        {
            _edges.TryRemove(EdgeKey(sourceId, targetId, relation), out _);
        }
        return Task.CompletedTask;
    }

    public Task<KnowledgeGraphView> QueryGraphAsync(string rootId, int depth = 2, CancellationToken ct = default)
        => GetGraphViewAsync(rootId, depth, ct);

    public Task<KnowledgeGraphView> GetGraphViewAsync(string? rootId = null, int depth = 2, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(TraverseSubgraph([.. _nodes.Values], [.. _edges.Values], rootId, depth));
    }

    public Task<IReadOnlyList<KnowledgeRecallHit>> SemanticSearchAsync(string query, int limit = 10, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) { return Task.FromResult<IReadOnlyList<KnowledgeRecallHit>>([]); }

        var terms = HybridMemoryScorer.Tokenize(query);
        var nodeScores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var node in _nodes.Values)
        {
            var text = $"{node.Label} {node.Type} {string.Join(" ", node.Properties.Select(p => $"{p.Key} {p.Value}"))}";
            var lexical = HybridMemoryScorer.LexicalScore(text, terms);
            if (lexical > 0)
            {
                nodeScores[node.Id] = lexical * (1.0 + (node.Importance / 10.0));
            }
        }

        var activated = new Dictionary<string, double>(nodeScores, StringComparer.Ordinal);
        foreach (var (sourceId, sourceScore) in nodeScores)
        {
            if (_nodes.TryGetValue(sourceId, out var sourceNode) && sourceNode.Vector is { Length: > 0 } sourceVec)
            {
                foreach (var candidate in _nodes.Values)
                {
                    if (!string.Equals(candidate.Id, sourceId, StringComparison.Ordinal)
                        && candidate.Vector is { Length: > 0 } candidateVec
                        && candidateVec.Length == sourceVec.Length)
                    {
                        var sim = HybridMemoryScorer.CosineSimilarity(sourceVec, candidateVec);
                        if (sim > 0) { activated[candidate.Id] = activated.GetValueOrDefault(candidate.Id) + (sourceScore * sim * 0.5); }
                    }
                }
            }

            foreach (var edge in _edges.Values)
            {
                if (string.Equals(edge.SourceId, sourceId, StringComparison.Ordinal))
                {
                    activated[edge.TargetId] = activated.GetValueOrDefault(edge.TargetId) + (sourceScore * edge.Weight * 0.5);
                }
                else if (string.Equals(edge.TargetId, sourceId, StringComparison.Ordinal))
                {
                    activated[edge.SourceId] = activated.GetValueOrDefault(edge.SourceId) + (sourceScore * edge.Weight * 0.5);
                }
            }
        }

        var hits = activated
            .OrderByDescending(kv => kv.Value)
            .Take(limit)
            .Select(kv => _nodes.TryGetValue(kv.Key, out var n) ? new KnowledgeRecallHit(n, kv.Value) : null)
            .OfType<KnowledgeRecallHit>()
            .ToList();
        return Task.FromResult<IReadOnlyList<KnowledgeRecallHit>>(hits);
    }

    internal static IReadOnlyList<KnowledgeEdge> FilterEdges(IEnumerable<KnowledgeEdge> source, string nodeId, EdgeDirection direction)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) { return []; }
        var filtered = direction switch
        {
            EdgeDirection.Outgoing => source.Where(e => string.Equals(e.SourceId, nodeId, StringComparison.Ordinal)),
            EdgeDirection.Incoming => source.Where(e => string.Equals(e.TargetId, nodeId, StringComparison.Ordinal)),
            _ => source.Where(e => string.Equals(e.SourceId, nodeId, StringComparison.Ordinal) || string.Equals(e.TargetId, nodeId, StringComparison.Ordinal)),
        };
        return filtered.ToList();
    }

    internal static KnowledgeGraphView TraverseSubgraph(
        IReadOnlyList<KnowledgeNode> nodes,
        IReadOnlyList<KnowledgeEdge> edges,
        string? rootId,
        int depth)
    {
        if (string.IsNullOrWhiteSpace(rootId))
        {
            return new KnowledgeGraphView(nodes, edges, rootId);
        }

        if (!nodes.Any(n => string.Equals(n.Id, rootId, StringComparison.Ordinal)))
        {
            return new KnowledgeGraphView([], [], rootId);
        }

        var maxDepth = Math.Clamp(depth, 0, 10);
        var visited = new HashSet<string>(StringComparer.Ordinal) { rootId };
        var traversed = new List<KnowledgeEdge>();
        var currentLevel = new HashSet<string>(StringComparer.Ordinal) { rootId };

        for (var d = 0; d < maxDepth && currentLevel.Count > 0; d++)
        {
            var nextLevel = new HashSet<string>(StringComparer.Ordinal);
            foreach (var currentId in currentLevel)
            {
                foreach (var edge in edges)
                {
                    if (string.Equals(edge.SourceId, currentId, StringComparison.Ordinal))
                    {
                        traversed.Add(edge);
                        if (visited.Add(edge.TargetId)) { nextLevel.Add(edge.TargetId); }
                    }
                    else if (string.Equals(edge.TargetId, currentId, StringComparison.Ordinal))
                    {
                        traversed.Add(edge);
                        if (visited.Add(edge.SourceId)) { nextLevel.Add(edge.SourceId); }
                    }
                }
            }
            currentLevel = nextLevel;
        }

        var resultNodes = nodes.Where(n => visited.Contains(n.Id)).ToList();
        var distinctEdges = traversed.GroupBy(e => (e.SourceId, e.TargetId, e.Relation)).Select(g => g.First()).ToList();
        return new KnowledgeGraphView(resultNodes, distinctEdges, rootId);
    }

    private static string EdgeKey(string source, string target, string relation)
        => $"{source}->{target}:{relation.ToLowerInvariant()}";
}
