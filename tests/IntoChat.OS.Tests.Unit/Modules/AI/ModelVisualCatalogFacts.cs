using DigitalBrain;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Testing;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Models.Anthropic;
using IntoChat.OS.Modules.AI.Models.Ollama;
using IntoChat.OS.Modules.AI.Pricing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.AI.Tests.Unit;

public sealed class ModelVisualCatalogFacts
{
    private sealed class FakeLocalRtxProbe : ILocalOllamaProbe
    {
        public Task<LocalOllamaDetection?> Detect(CancellationToken ct = default)
            => Task.FromResult<LocalOllamaDetection?>(new LocalOllamaDetection(
                "http://127.0.0.1:11434",
                "qwen2.5-coder:14b",
                ["qwen2.5-coder:14b", "qwen3:14b"],
                HasNomicEmbed: true));
    }

    [Fact]
    public void Sonnet46AndOpus46AreRegisteredAsFirstClassAnthropicModelsWithPricing()
    {
        var sonnet46 = Assert.Single(LLMModel.All, static m => m is Sonnet46);
        Assert.Equal("claude-sonnet-4-6", sonnet46.Id);
        Assert.Equal(AiProvider.Anthropic, sonnet46.Provider);
        Assert.True(sonnet46.SupportsTools);
        Assert.Equal(typeof(ISonnet46), sonnet46.Marker);

        var opus46 = Assert.Single(LLMModel.All, static m => m is Opus46);
        Assert.Equal("claude-opus-4-6", opus46.Id);
        Assert.Equal(AiProvider.Anthropic, opus46.Provider);
        Assert.True(opus46.SupportsTools);
        Assert.Equal(typeof(IOpus46), opus46.Marker);

        Assert.Same(sonnet46, LLMModel.Find("claude-sonnet-4-6"));
        Assert.Same(sonnet46, LLMModel.Find("sonnet-4.6"));
        Assert.Same(sonnet46, LLMModel.Find("ISonnet46"));
        Assert.Same(opus46, LLMModel.Find("claude-opus-4-6"));
        Assert.Same(opus46, LLMModel.Find("opus-4.6"));
        Assert.Same(opus46, LLMModel.Find("IOpus46"));

        var prices = new ModelPriceList();
        Assert.NotNull(prices.MicrosPerUnit(CostKind.Model, "claude-sonnet-4-6:input"));
        Assert.NotNull(prices.MicrosPerUnit(CostKind.Model, "claude-sonnet-4-6:output"));
        Assert.NotNull(prices.MicrosPerUnit(CostKind.Model, "claude-opus-4-6:input"));
        Assert.NotNull(prices.MicrosPerUnit(CostKind.Model, "claude-opus-4-6:output"));
    }

    [Fact]
    public async Task ModelCatalogAndNeuronListEnrichedVisualLabelsIconsAndLocalOllamaProviders()
    {
        var services = new ServiceCollection();
        services.AddOptions<AIOptions>();
        services.AddSingleton<IAiCredentials>(new FixedAiCredentials().Ready("anthropic", "test-key"));
        services.AddSingleton<IPriceList, ModelPriceList>();
        services.AddSingleton<ILocalOllamaProbe, FakeLocalRtxProbe>();
        AIClients.Add(services);
        services.AddSingleton<ModelCatalog>();

        await using var provider = services.BuildServiceProvider();
        await using var os = TestOS.Local(options => options.Services = provider);
        os.Register(typeof(AIModule).Assembly);

        var catalogNeuron = os.Get<IModelCatalog>(new NeuronId(new BrainId("local"), "ai.models", "main"));
        var choices = await catalogNeuron.List();

        var localQwen = Assert.Single(choices.Models, static c => c.Model == "qwen2.5-coder:14b");
        Assert.Equal("Qwen 2.5 Coder 14B", localQwen.Label);
        Assert.Equal("provider:ollama", localQwen.IconKey);
        Assert.Equal("Ollama (Local GPU)", localQwen.ProviderLabel);
        Assert.Equal("#6366F1", localQwen.AccentHex);
        Assert.True(localQwen.Available);

        var localQwen3 = Assert.Single(choices.Models, static c => c.Model == "qwen3:14b");
        Assert.Equal("Qwen 3 14B", localQwen3.Label);
        Assert.Equal("provider:ollama", localQwen3.IconKey);
        Assert.Equal("Ollama (Local GPU)", localQwen3.ProviderLabel);

        var sonnet46 = Assert.Single(choices.Models, static c => c.Model == "claude-sonnet-4-6");
        Assert.Equal("Sonnet 4.6", sonnet46.Label);
        Assert.Equal("provider:anthropic", sonnet46.IconKey);
        Assert.Equal("Anthropic", sonnet46.ProviderLabel);
        Assert.Equal("#D97757", sonnet46.AccentHex);
        Assert.True(sonnet46.Available);

        var opus46 = Assert.Single(choices.Models, static c => c.Model == "claude-opus-4-6");
        Assert.Equal("Opus 4.6", opus46.Label);
        Assert.Equal("provider:anthropic", opus46.IconKey);
        Assert.Equal("Anthropic", opus46.ProviderLabel);
        Assert.Equal("#D97757", opus46.AccentHex);

        var gpt54 = Assert.Single(choices.Models, static c => c.Model == "gpt-5.4");
        Assert.Equal("GPT-5.4", gpt54.Label);
        Assert.Equal("provider:openai", gpt54.IconKey);
        Assert.Equal("OpenAI", gpt54.ProviderLabel);
        Assert.Equal("#10A37F", gpt54.AccentHex);
    }

    [Fact]
    public async Task ModelCatalogResolvesAutoDetectedLocalOllamaWhenOnlyRebuildingChatClientIsRegistered()
    {
        var services = new ServiceCollection();
        services.AddOptions<AIOptions>();
        services.AddSingleton<IAiCredentials>(new FixedAiCredentials());
        services.AddSingleton<IPriceList, ModelPriceList>();
        services.AddSingleton<ILocalOllamaProbe, FakeLocalRtxProbe>();
        AIClients.Add(services);
        services.AddSingleton<ModelCatalog>();

        await using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<ModelCatalog>();

        var resolvedQwen = await catalog.Resolve("local", new ModelSelection("qwen2.5-coder:14b"), requiresTools: true, TestContext.Current.CancellationToken);
        Assert.Equal(AiProvider.Ollama, resolvedQwen.Provider);
        Assert.Equal("qwen2.5-coder:14b", resolvedQwen.Model);
        Assert.Equal("http://127.0.0.1:11434/", resolvedQwen.Endpoint);

        var resolvedAuto = await catalog.Resolve("local", null, requiresTools: true, TestContext.Current.CancellationToken);
        Assert.Equal(AiProvider.Ollama, resolvedAuto.Provider);
        Assert.Equal("qwen2.5-coder:14b", resolvedAuto.Model);
        Assert.Equal("http://127.0.0.1:11434/", resolvedAuto.Endpoint);
    }

    [Fact]
    public async Task Sonnet46AndOpus46ModelNeuronsDispatchOnTestOS()
    {
        await using var fixture = new IntoChat.OS.Modules.AI.Tests.AiFixture();
        var req = new InferenceRequest([new AiMessage("user", [new AiText("ping")])]);

        var sonnet = fixture.Runtime.Get<ISonnet46>(new NeuronId(new BrainId("local"), "ai.llm.sonnet46", "main"));
        var reply = await sonnet.Generate(req, TestContext.Current.CancellationToken);
        Assert.Equal("stop", reply.FinishReason);

        var opus = fixture.Runtime.Get<IOpus46>(new NeuronId(new BrainId("local"), "ai.llm.opus46", "main"));
        var opusReply = await opus.Generate(req, TestContext.Current.CancellationToken);
        Assert.Equal("stop", opusReply.FinishReason);
    }
}
