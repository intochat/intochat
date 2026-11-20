using DigitalBrain.Caching;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI.Memory;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.AI.Agents.Tools;

// Ranks an agent's tool pool by cosine similarity against "{Name}: {Description}", keeping at most K tools per turn.
// Falls back cleanly to the local deterministic hash embedder when no embedding generator is configured or when it throws.
internal sealed class ToolRouter(
    IReadOnlyList<AIFunction> tools,
    IEmbeddingGenerator<string, Embedding<float>>? embeddings) : IToolRouter
{
    private static readonly BoundedCache<string, float[]> VectorCache = new(1_024, StringComparer.Ordinal);

    public async Task<IReadOnlyList<AIFunction>> Route(string text, int k = 8, CancellationToken ct = default)
    {
        if (k <= 0 || tools.Count == 0)
        {
            return [];
        }

        if (tools.Count <= 2 * k)
        {
            return tools;
        }

        if (embeddings is not null)
        {
            try
            {
                var routed = await RouteWithGenerator(embeddings, text ?? string.Empty, k, ct).ConfigureAwait(false);
                if (routed is not null)
                {
                    return routed;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                OsConsoleLog.For<ToolRouter>().LogSwallowed(error, "tool router embedding failed; falling back to local hash embedder");
            }
        }

        return RouteLocally(text ?? string.Empty, k);
    }

    private async Task<IReadOnlyList<AIFunction>?> RouteWithGenerator(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        string query,
        int k,
        CancellationToken ct)
    {
        var providerKey = generator.GetType().FullName ?? "embedder";
        var vectors = new float[tools.Count][];
        var keys = new string[tools.Count];
        var missingIndices = new List<int>();
        var missingTexts = new List<string>();

        for (var i = 0; i < tools.Count; i++)
        {
            var toolText = ToolText(tools[i]);
            var cacheKey = providerKey + "\0" + toolText;
            keys[i] = cacheKey;
            if (VectorCache.TryGet(cacheKey, out var cached))
            {
                vectors[i] = cached;
            }
            else
            {
                missingIndices.Add(i);
                missingTexts.Add(toolText);
            }
        }

        if (missingTexts.Count > 0)
        {
            var generated = await generator.GenerateAsync(missingTexts, cancellationToken: ct).ConfigureAwait(false);
            if (generated.Count != missingTexts.Count)
            {
                return null;
            }
            for (var i = 0; i < missingIndices.Count; i++)
            {
                var vector = generated[i].Vector.ToArray();
                if (vector.Length == 0)
                {
                    return null;
                }
                var index = missingIndices[i];
                VectorCache.Set(keys[index], vector);
                vectors[index] = vector;
            }
        }

        var queryResult = await generator.GenerateAsync([query], cancellationToken: ct).ConfigureAwait(false);
        if (queryResult.Count == 0)
        {
            return null;
        }
        var queryVector = queryResult[0].Vector.ToArray();
        if (queryVector.Length == 0)
        {
            return null;
        }

        for (var i = 0; i < tools.Count; i++)
        {
            if (vectors[i] is null || vectors[i].Length != queryVector.Length)
            {
                return null;
            }
        }

        return TopK(queryVector, vectors, k);
    }

    private IReadOnlyList<AIFunction> RouteLocally(string query, int k)
    {
        var queryVector = HybridMemoryEmbedder.LocalDeterministicEmbed(query.Replace('_', ' '));
        var vectors = new float[tools.Count][];
        for (var i = 0; i < tools.Count; i++)
        {
            var toolText = ToolText(tools[i]);
            var cacheKey = "local\0" + toolText;
            vectors[i] = VectorCache.GetOrAdd(
                cacheKey,
                _ => HybridMemoryEmbedder.LocalDeterministicEmbed(toolText.Replace('_', ' ')));
        }

        return TopK(queryVector, vectors, k);
    }

    private IReadOnlyList<AIFunction> TopK(float[] queryVector, float[][] toolVectors, int k)
        => [.. tools
            .Select((tool, index) => (Tool: tool, Score: HybridMemoryScorer.Cosine(queryVector, toolVectors[index])))
            .OrderByDescending(static entry => entry.Score)
            .ThenBy(static entry => entry.Tool.Name, StringComparer.Ordinal)
            .Take(k)
            .Select(static entry => entry.Tool)];

    private static string ToolText(AIFunction tool) => $"{tool.Name}: {tool.Description}";
}
