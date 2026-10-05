using DigitalBrain.Apps;
using DigitalBrain.Framework;
using DigitalBrain.Time;
using DigitalBrain.Qdrant;
using DigitalBrain.Framework.Vectors;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Apps.Tests;

public sealed class BuilderContractSearchFacts
{
    [Fact]
    public async Task BuilderSearchPreservesTheTimerQueryResult()
    {
        await using var brain = await ModuleTest.Create().WithModule<TimeModule>().WithReminders()
            .ConfigureSilo(silo => silo.Services.AddSingleton<IVectorStore>(new InMemoryQdrant())
                .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new TimerEmbeddings()))
            .StartAsync(TestContext.Current.CancellationToken);
        var result = await BuilderTools.Search(brain.SiloServices.GetRequiredService<ContractCatalog>(), brain.SiloServices.GetRequiredService<NeuronTypeSearch>(), "schedule a timer");
        Assert.Equal("time", result[0].Module);
        Assert.Equal("DigitalBrain.Time.Timers.ITimer", result[0].Contract);
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
