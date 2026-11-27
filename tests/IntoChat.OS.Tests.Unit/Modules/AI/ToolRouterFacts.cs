using System.Text.Json;
using DigitalBrain.Caching;
using DigitalBrain.Contracts;
using IntoChat.OS.Modules.AI.Agents.Tools;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Tests.Unit.Modules.AI;

public sealed class ToolRouterFacts
{
    private static IReadOnlyList<AIFunction> Fake(int count, string descriptionPrefix = "Fake tool number") =>
        [.. Enumerable.Range(1, count).Select(i =>
            (AIFunction)new ContractTool(
                new ToolSchema("Sample.IFake", $"fake_tool_{i}", $"Method{i}", $"{descriptionPrefix} {i}.", """{"type":"object","properties":{}}""", "fake/key", null, true),
                static (_, _, _, _, _) => Task.FromResult<JsonElement?>(null)))];

    private static IReadOnlyList<AIFunction> InoLikeTools()
    {
        Type[] contracts = [typeof(IFiles), typeof(IFocus), typeof(IData), typeof(IStudio), typeof(IUserSettings)];
        return
        [
            .. contracts.SelectMany(ToolSchemas.Of).Select(schema =>
                (AIFunction)new ContractTool(schema, static (_, _, _, _, _) => Task.FromResult<JsonElement?>(null))),
        ];
    }

    private sealed class ThrowingEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Embedding service offline.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private class CountingEmbedder(string modelId) : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly EmbeddingGeneratorMetadata _metadata = new("test", defaultModelId: modelId);
        public List<string[]> Batches { get; } = [];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = values.ToArray();
            Batches.Add(list);
            var embeddings = new GeneratedEmbeddings<Embedding<float>>(
                list.Select(text => new Embedding<float>(new float[] { (text.Length % 17) + 1f, 1f, 0.5f })));
            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata : null;

        public void Dispose() { }
    }

    private sealed class AlternateCountingEmbedder(string modelId) : CountingEmbedder(modelId);

    [Fact]
    public async Task RouterReturnsAllWhenFew() =>
        Assert.Equal(10, (await new ToolRouter(Fake(10), null).Route("anything", k: 8, TestContext.Current.CancellationToken)).Count);

    [Fact]
    public async Task RouterPicksTheFileToolForAFileRequest()
    {
        var routed = await new ToolRouter(InoLikeTools(), null)
            .Route("write a new text file called groceries.txt", k: 4, TestContext.Current.CancellationToken);
        Assert.Contains(routed, static t => t.Name == "files_write");
    }

    [Fact]
    public async Task RouterSurvivesAThrowingEmbedder() =>
        Assert.Equal(4, (await new ToolRouter(Fake(30), new ThrowingEmbedder()).Route("x", k: 4, TestContext.Current.CancellationToken)).Count);

    [Fact]
    public async Task RepeatedTurnsWithSameTools_EmbedToolSchemasOnlyOnce()
    {
        var cache = new BoundedCache<string, float[]>(128, StringComparer.Ordinal);
        var embedder = new CountingEmbedder("text-embedding-3-small");
        var tools = Fake(20);

        await new ToolRouter(tools, embedder, cache).Route("first query", k: 4, TestContext.Current.CancellationToken);
        await new ToolRouter(tools, embedder, cache).Route("second query", k: 4, TestContext.Current.CancellationToken);

        // Turn 1: batch of 20 tools + batch of 1 query. Turn 2: 0 tool batches + batch of 1 query.
        Assert.Equal(3, embedder.Batches.Count);
        Assert.Equal(20, embedder.Batches[0].Length);
        Assert.Equal(["first query"], embedder.Batches[1]);
        Assert.Equal(["second query"], embedder.Batches[2]);
    }

    [Fact]
    public async Task ChangingToolDescriptionOrModelOrGeneratorType_MissesCacheProperly()
    {
        var cache = new BoundedCache<string, float[]>(128, StringComparer.Ordinal);
        var embedderA = new CountingEmbedder("model-a");
        var toolsV1 = Fake(20, "Version 1 tool");

        await new ToolRouter(toolsV1, embedderA, cache).Route("q", k: 4, TestContext.Current.CancellationToken);
        Assert.Equal(2, embedderA.Batches.Count);

        // Changing description invalidates all 20 tool entries.
        var toolsV2 = Fake(20, "Version 2 tool");
        await new ToolRouter(toolsV2, embedderA, cache).Route("q", k: 4, TestContext.Current.CancellationToken);
        Assert.Equal(4, embedderA.Batches.Count);
        Assert.Equal(20, embedderA.Batches[2].Length);

        // Changing DefaultModelId on the same CLR type misses cache.
        var embedderB = new CountingEmbedder("model-b");
        await new ToolRouter(toolsV2, embedderB, cache).Route("q", k: 4, TestContext.Current.CancellationToken);
        Assert.Equal(2, embedderB.Batches.Count);
        Assert.Equal(20, embedderB.Batches[0].Length);

        // Changing CLR generator type with the same modelId misses cache.
        var altEmbedder = new AlternateCountingEmbedder("model-b");
        await new ToolRouter(toolsV2, altEmbedder, cache).Route("q", k: 4, TestContext.Current.CancellationToken);
        Assert.Equal(2, altEmbedder.Batches.Count);
        Assert.Equal(20, altEmbedder.Batches[0].Length);
    }

    [Fact]
    public async Task CacheStaysBoundedUnderHighCardinality()
    {
        var cache = new BoundedCache<string, float[]>(8, StringComparer.Ordinal);
        var embedder = new CountingEmbedder("bounded-model");
        var tools = Fake(20);

        await new ToolRouter(tools, embedder, cache).Route("q", k: 4, TestContext.Current.CancellationToken);

        Assert.Equal(8, cache.Count);
    }
}
