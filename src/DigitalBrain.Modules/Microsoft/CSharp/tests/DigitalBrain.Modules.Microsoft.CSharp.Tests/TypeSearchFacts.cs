using DigitalBrain.Framework;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using DigitalBrain.Framework.Contracts;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class TypeSearchFacts
{
    [Fact]
    public async Task WithoutAnEmbeddingModelSearchMatchesWords()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<RegistryFixtureModule>().StartAsync(ct);
        var hit = Assert.Single(await brain.SiloServices.GetRequiredService<NeuronTypeSearch>().Search("registry-clock", 1, ct));
        Assert.Equal("test.registry-clock", hit.Type.Id);
    }

    [Fact]
    public async Task VectorSearchReturnsTypesAndCanBeRebuiltAfterAnEmbeddingFailure()
    {
        var embedder = new ClockEmbeddings { Fail = true };
        await using var brain = await ModuleTest.Create().WithModule<RegistryFixtureModule>()
            .ConfigureSilo(silo => silo.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(embedder))
            .StartAsync(TestContext.Current.CancellationToken);
        var catalog = brain.SiloServices.GetRequiredService<ContractCatalog>();
        var search = brain.SiloServices.GetRequiredService<NeuronTypeSearch>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => search.Search("wake me tomorrow", 1, TestContext.Current.CancellationToken));
        Assert.Contains(catalog.Read(), type => type.Id == "test.registry-clock");
        embedder.Fail = false;
        var found = Assert.Single(await search.Search("wake me tomorrow", 1, TestContext.Current.CancellationToken));
        Assert.Equal("test.registry-clock", found.Type.Id);
        Assert.Equal(1, found.Score, precision: 4);
        Assert.Contains("Ping", Assert.Single(found.Type.Methods), StringComparison.Ordinal);
    }

    private sealed class ClockEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        public bool Fail { get; set; }
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
            => Fail ? throw new InvalidOperationException("embedding service unavailable")
                : Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(text =>
                    new Embedding<float>(text.Contains("wake", StringComparison.OrdinalIgnoreCase) ? new float[] { 1, 0 } : [0, 1]))));
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
