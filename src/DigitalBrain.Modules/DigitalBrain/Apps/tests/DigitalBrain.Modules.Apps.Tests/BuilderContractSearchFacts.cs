using DigitalBrain.Apps;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Testing;
using DigitalBrain.Time;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Apps.Tests;

public sealed class BuilderContractSearchFacts
{
    [Fact]
    public async Task BuilderSearchPreservesTheTimerQueryResult()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<TimeModule>()
            // The fake model server speaks chat only; a fixed embedder stands in for an embeddings endpoint.
            .ConfigureServices(services => services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new TimerEmbeddings()))
            .StartAsync(ct);
        var result = await BuilderTools.Search(brain.Services.GetRequiredService<NeuronTypeSearch>(), "schedule a timer", ct);
        Assert.Equal(new BuilderTools.ContractHit("time", "DigitalBrain.Time.Timers.ITimer", "Timer"), result[0]);
    }

    private sealed class TimerEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(text =>
                new Embedding<float>(text.StartsWith("Timer:", StringComparison.Ordinal) || text == "schedule a timer" ? new float[] { 1, 0 } : [0, 1]))));
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
