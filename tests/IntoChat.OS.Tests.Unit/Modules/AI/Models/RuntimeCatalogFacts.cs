using System.Runtime.CompilerServices;
using DigitalBrain.Capabilities.Compute;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Pricing;
using IntoChat.OS.Modules.Devices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Modules.AI.Models;

public sealed class RuntimeCatalogFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class CapturingChatClient(Action onCall) : IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            onCall();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeEmbeddingGenerator(float[] vector, Action onGenerate) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            onGenerate();
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(vector)]));
        }
    }

    private sealed class FakeRuntime(RuntimeId id) : ILocalRuntime
    {
        public RuntimeId Id { get; } = id;
        public bool Reachable { get; set; } = true;
        public Uri? Endpoint { get; set; } = new("http://127.0.0.1:11434");
        public List<ModelCard> InstalledCards { get; } = [];
        public ModelId? LastChat { get; private set; }
        public int ChatCalls { get; private set; }
        public ModelId? LastEmbed { get; private set; }
        public float[] EmbedVector { get; set; } = [3f, 4f];

        public Task<RuntimeStatus> Probe(CancellationToken ct)
            => Task.FromResult(new RuntimeStatus(Id, Reachable, Reachable ? "0.9.0" : null, Endpoint));

        public async IAsyncEnumerable<ModelCard> Catalog([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var card in InstalledCards)
            {
                yield return card;
            }
        }

        public async IAsyncEnumerable<ModelCard> Installed([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            if (!Reachable)
            {
                yield break;
            }
            foreach (var card in InstalledCards)
            {
                yield return card;
            }
        }

        public async IAsyncEnumerable<InstallProgress> Install(ModelId id, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task Remove(ModelId id, CancellationToken ct) => Task.CompletedTask;

        public IChatClient Chat(ModelId id)
        {
            LastChat = id;
            return new CapturingChatClient(() => ChatCalls++);
        }

        public IEmbeddingGenerator<string, Embedding<float>>? Embed(ModelId id)
        {
            LastEmbed = id;
            return new FakeEmbeddingGenerator(EmbedVector, static () => { });
        }
    }

    private static (ServiceProvider Services, ModelCatalog Catalog) CreateCatalog(params ILocalRuntime[] runtimes)
    {
        var services = new ServiceCollection();
        services.AddOptions<AIOptions>();
        services.AddSingleton<IAiCredentials>(new FixedAiCredentials());
        services.AddSingleton<IPriceList, ModelPriceList>();
        foreach (var runtime in runtimes)
        {
            services.AddSingleton<ILocalRuntime>(runtime);
        }
        AIClients.Add(services);
        services.AddSingleton<ModelCatalog>();

        var provider = services.BuildServiceProvider();
        return (provider, provider.GetRequiredService<ModelCatalog>());
    }

    [Fact]
    public async Task InstalledRuntimeModelsAreListed()
    {
        var runtime = new FakeRuntime(RuntimeId.Ollama);
        runtime.InstalledCards.Add(new ModelCard(
            new ModelId("llama3.2:3b"),
            AiProvider.Ollama,
            "Llama",
            "Llama 3.2B",
            Where.Device,
            Abilities.Text | Abilities.Tools,
            Tier.Fast,
            Bytes: 2_000_000_000L));

        await using var sp = CreateCatalog(runtime).Services;
        var catalog = sp.GetRequiredService<ModelCatalog>();

        var cards = await catalog.List(ct: Ct);
        Assert.Contains(cards, static c => c.Id.Value == "llama3.2:3b" && c.Name == "Llama 3.2B");
    }

    [Fact]
    public async Task ANotInstalledDeviceCardSaysSo()
    {
        var runtime = new FakeRuntime(RuntimeId.Ollama);
        runtime.InstalledCards.Add(KnownModels.Qwen35 with { Bytes = 2_500_000_000L });

        var (sp, catalog) = CreateCatalog(runtime);
        await using var _ = sp;

        var availability = await catalog.Availability(brain: null, Ct);
        var gemma = Assert.Single(availability, static a => a.Model == KnownModels.Gemma4.Id);
        Assert.False(gemma.Ready);
        Assert.Equal("Not installed", gemma.Reason);

        var qwen = Assert.Single(availability, static a => a.Model == KnownModels.Qwen35.Id);
        Assert.True(qwen.Ready);
        Assert.Null(qwen.Reason);
    }

    [Fact]
    public async Task UnreachableOllamaReportsNotRunning()
    {
        var runtime = new FakeRuntime(RuntimeId.Ollama) { Reachable = false };
        var (sp, catalog) = CreateCatalog(runtime);
        await using var _ = sp;

        var availability = await catalog.Availability(brain: null, Ct);
        var gemma = Assert.Single(availability, static a => a.Model == KnownModels.Gemma4.Id);
        Assert.False(gemma.Ready);
        Assert.Equal("Ollama is not running", gemma.Reason);
    }

    [Fact]
    public async Task DeviceChatComesFromTheRuntime()
    {
        var runtime = new FakeRuntime(RuntimeId.Ollama);
        runtime.InstalledCards.Add(KnownModels.Qwen35);

        var (sp, catalog) = CreateCatalog(runtime);
        await using var _ = sp;

        var resolved = await catalog.Decide("local", Role.Chat, new ModelSelection(KnownModels.Qwen35.Id.Value), ct: Ct);
        using var client = await catalog.CreateClient("local", resolved, Ct);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: Ct);

        Assert.Equal("ok", response.Text);
        Assert.Equal(KnownModels.Qwen35.Id, runtime.LastChat);
        Assert.Equal(1, runtime.ChatCalls);
    }

    [Fact]
    public async Task FoundryAndOpenAiCompatChatComeFromTheirRuntimes()
    {
        var foundry = new FakeRuntime(RuntimeId.FoundryLocal) { Endpoint = new Uri("http://127.0.0.1:5272/v1") };
        var phi = new ModelCard(new ModelId("phi-4-mini"), AiProvider.FoundryLocal, "Phi", "Phi 4 Mini", Where.Device, Abilities.Text | Abilities.Tools, Tier.Fast);
        foundry.InstalledCards.Add(phi);

        var compat = new FakeRuntime(RuntimeId.OpenAiCompat("lmstudio")) { Endpoint = new Uri("http://127.0.0.1:1234/v1") };
        var local = new ModelCard(new ModelId("mistral-7b"), AiProvider.OpenAICompatible, "lmstudio", "mistral-7b", Where.Device, Abilities.Text | Abilities.Tools, Tier.Balanced);
        compat.InstalledCards.Add(local);

        var (sp, catalog) = CreateCatalog(foundry, compat);
        await using var scope = sp;

        var r1 = await catalog.Decide("local", Role.Chat, new ModelSelection("phi-4-mini"), ct: Ct);
        using var c1 = await catalog.CreateClient("local", r1, Ct);
        _ = await c1.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: Ct);
        Assert.Equal(new ModelId("phi-4-mini"), foundry.LastChat);

        var r2 = await catalog.Decide("local", Role.Chat, new ModelSelection("mistral-7b"), ct: Ct);
        using var c2 = await catalog.CreateClient("local", r2, Ct);
        _ = await c2.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: Ct);
        Assert.Equal(new ModelId("mistral-7b"), compat.LastChat);
    }

    [Fact]
    public void TheProbeIsGone()
        => Assert.Null(typeof(AIModule).Assembly.GetType("IntoChat.OS.Modules.AI.Models.Ollama.HttpLocalOllamaProbe"));

    [Fact]
    public async Task HybridMemoryEmbedderUsesOllamaRuntimeEmbed()
    {
        var runtime = new FakeRuntime(RuntimeId.Ollama) { EmbedVector = [3f, 4f] };
        runtime.InstalledCards.Add(KnownModels.EmbeddingGemma);

        var embedder = new HybridMemoryEmbedder([runtime]);
        var vector = await embedder.EmbedAsync("remember this", Ct);

        Assert.Equal(KnownModels.EmbeddingGemma.Id, runtime.LastEmbed);
        Assert.Equal(2, vector.Length);
        Assert.Equal(0.6f, vector[0], precision: 4);
        Assert.Equal(0.8f, vector[1], precision: 4);
    }
}
