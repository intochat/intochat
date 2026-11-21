using DigitalBrain;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Contracts;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Models.Ollama;
using IntoChat.OS.Modules.AI.Pricing;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Visuals;

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
        var sonnet46 = KnownModels.Sonnet46;
        Assert.Equal("claude-sonnet-4-6", sonnet46.Id.Value);
        Assert.Equal(AiProvider.Anthropic, sonnet46.Provider);
        Assert.True(sonnet46.Can.HasFlag(Abilities.Tools));

        var opus46 = KnownModels.Opus46;
        Assert.Equal("claude-opus-4-6", opus46.Id.Value);
        Assert.Equal(AiProvider.Anthropic, opus46.Provider);
        Assert.True(opus46.Can.HasFlag(Abilities.Tools));

        Assert.Same(sonnet46, KnownModels.Find("claude-sonnet-4-6"));
        Assert.Same(sonnet46, KnownModels.Find("sonnet-4.6"));
        Assert.Same(sonnet46, KnownModels.Find("ISonnet46"));
        Assert.Same(opus46, KnownModels.Find("claude-opus-4-6"));
        Assert.Same(opus46, KnownModels.Find("opus-4.6"));
        Assert.Same(opus46, KnownModels.Find("IOpus46"));

        var prices = new ModelPriceList();
        Assert.NotNull(prices.MicrosPerUnit(CostKind.Model, "claude-sonnet-4-6:input"));
        Assert.NotNull(prices.MicrosPerUnit(CostKind.Model, "claude-sonnet-4-6:output"));
        Assert.NotNull(prices.MicrosPerUnit(CostKind.Model, "claude-opus-4-6:input"));
        Assert.NotNull(prices.MicrosPerUnit(CostKind.Model, "claude-opus-4-6:output"));
    }

    [Fact]
    public async Task ModelCatalogAndNeuronListEnrichedVisualLabelsIconsAndLocalOllamaProviders()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
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
        var cards = await catalogNeuron.List();
        var availability = (await catalogNeuron.Availability()).ToDictionary(static a => a.Model.Value, StringComparer.OrdinalIgnoreCase);

        var localQwen = Assert.Single(cards, static c => c.Id.Value == "qwen2.5-coder:14b");
        Assert.Equal("Qwen 2.5 Coder 14B", localQwen.Name);
        Assert.Equal("provider:ollama", localQwen.IconKey());
        Assert.True(availability[localQwen.Id.Value].Ready);

        var localQwen3 = Assert.Single(cards, static c => c.Id.Value == "qwen3:14b");
        Assert.Equal("qwen3:14b", localQwen3.Name);
        Assert.Equal("provider:ollama", localQwen3.IconKey());
        Assert.True(availability[localQwen3.Id.Value].Ready);

        var sonnet46 = Assert.Single(cards, static c => c.Id.Value == "claude-sonnet-4-6");
        Assert.Equal("Sonnet 4.6", sonnet46.Name);
        Assert.Equal("provider:anthropic", sonnet46.IconKey());
        Assert.True(availability[sonnet46.Id.Value].Ready);

        var opus46 = Assert.Single(cards, static c => c.Id.Value == "claude-opus-4-6");
        Assert.Equal("Opus 4.6", opus46.Name);
        Assert.Equal("provider:anthropic", opus46.IconKey());
        Assert.True(availability[opus46.Id.Value].Ready);

        var gpt54 = Assert.Single(cards, static c => c.Id.Value == "gpt-5.4");
        Assert.Equal("GPT-5.4", gpt54.Name);
        Assert.Equal("provider:openai", gpt54.IconKey());
        Assert.False(availability[gpt54.Id.Value].Ready);
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
        Assert.Contains(resolvedAuto.Model, new[] { "qwen2.5-coder:14b", "qwen3:14b" });
        Assert.Equal("http://127.0.0.1:11434/", resolvedAuto.Endpoint);

        var resolvedCode = await catalog.Decide("local", Role.Think, null, requiresTools: true, TestContext.Current.CancellationToken);
        Assert.Equal(AiProvider.Ollama, resolvedCode.Provider);
        Assert.Equal("qwen2.5-coder:14b", resolvedCode.Model);
    }

    [Fact]
    public async Task Sonnet46AndOpus46ModelNeuronsDispatchOnTestOS()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var fixture = new IntoChat.OS.Modules.AI.Tests.AiFixture();
        var req = new InferenceRequest([new AiMessage("user", [new AiText("ping")])]);

        var sonnet = fixture.Runtime.Get<ILLM>(PlainNeuronContracts.IdOf(typeof(ILLM), ILLM.Key("local", KnownModels.Sonnet46.Id)));
        var reply = await sonnet.Generate(req, TestContext.Current.CancellationToken);
        Assert.Equal("stop", reply.FinishReason);

        var opus = fixture.Runtime.Get<ILLM>(PlainNeuronContracts.IdOf(typeof(ILLM), ILLM.Key("local", KnownModels.Opus46.Id)));
        var opusReply = await opus.Generate(req, TestContext.Current.CancellationToken);
        Assert.Equal("stop", opusReply.FinishReason);
    }

    [Fact]
    public void EveryCardIconIsAllowlisted()
        => Assert.All(KnownModels.All, c => Assert.Contains(c.IconKey(), (IReadOnlySet<string>)VisualCatalog.AllIconKeys));

    [Fact]
    public void VisualCatalogHasNoModelLookup()
        => Assert.Null(typeof(VisualCatalog).GetMethod("ForModel"));
}
