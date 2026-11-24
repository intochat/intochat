using System.Runtime.CompilerServices;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Pricing;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Devices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Modules.AI.Models;

public sealed class PickFacts
{
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private sealed class FakeRuntime(params ModelCard[] cards) : ILocalRuntime
    {
        public RuntimeId Id => RuntimeId.Ollama;

        public Task<RuntimeStatus> Probe(CancellationToken ct)
            => Task.FromResult(new RuntimeStatus(Id, Reachable: true, Version: "0.9.0", Endpoint: new Uri("http://127.0.0.1:11434")));

        public async IAsyncEnumerable<ModelCard> Catalog([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var card in cards) { yield return card; }
        }

        public async IAsyncEnumerable<ModelCard> Installed([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var card in cards)
            {
                ct.ThrowIfCancellationRequested();
                yield return card;
            }
        }

        public async IAsyncEnumerable<InstallProgress> Install(ModelId id, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task Remove(ModelId id, CancellationToken ct) => Task.CompletedTask;

        public IChatClient Chat(ModelId id) => throw new NotSupportedException();
    }

    private sealed class TestOSProxies(TestOS os) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class => os.Get<T>(id);
        public object Get(Type contract, NeuronId id) => throw new NotSupportedException();
    }

    private static async Task<(TestOS Os, ServiceProvider Services, IModelCatalog Catalog, IUserSettings Settings)> StartHost(
        string topology = IntoChatOSNames.TopologyCloud,
        Action<AIOptions>? configureAi = null,
        FixedAiCredentials? credentials = null,
        IEnumerable<ILocalRuntime>? runtimes = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [IntoChatOSNames.TopologyKey] = topology,
            })
            .Build();

        TestOS? osRef = null;
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddOptions<AIOptions>().Configure(o => configureAi?.Invoke(o));
        services.AddSingleton<IAiCredentials>(credentials ?? new FixedAiCredentials());
        services.AddSingleton<IPriceList, ModelPriceList>();
        if (runtimes is not null)
        {
            foreach (var runtime in runtimes)
            {
                services.AddSingleton(runtime);
            }
        }
        AIClients.Add(services);
        services.AddSingleton<INeuronProxies>(_ => new TestOSProxies(osRef!));

        var provider = services.BuildServiceProvider();
        var os = TestOS.Local(options => options.Services = provider);
        osRef = os;
        os.Register(typeof(AIModule).Assembly);
        os.Register<UserSettingsNeuron>();

        var brain = new BrainId("local");
        var catalog = os.Get<IModelCatalog>(new NeuronId(brain, "ai.models", "main"));
        var settings = os.Get<IUserSettings>(new NeuronId(brain, "os.settings", "settings"));
        return (os, provider, catalog, settings);
    }

    [Theory]
    [InlineData("turn", "gpt-5.4", "claude-sonnet-5", "gpt-5.4")]
    [InlineData("person", null, "claude-sonnet-5", "claude-sonnet-5")]
    [InlineData("deployment", null, null, "deepseek/deepseek-v4.1-flash")]
    public async Task PickOrder(string _, string? turn, string? person, string expected)
    {
        using var caller = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));

        var creds = new FixedAiCredentials()
            .Ready("openai", "sk-openai")
            .Ready("anthropic", "sk-anthropic")
            .Ready("openrouter", "sk-openrouter");

        var (os, sp, catalog, settings) = await StartHost(
            topology: IntoChatOSNames.TopologyCloud,
            configureAi: ai => ai.Roles.Cloud[ModelRole.Chat] = KnownModels.DeepSeekV41Flash.Id.Value,
            credentials: creds);

        await using var _os = os;
        await using var _sp = sp;

        if (person is not null)
        {
            await settings.SetRole(ModelRole.Chat, new(person));
        }

        var picked = await catalog.Pick(ModelRole.Chat, turn is null ? null : new(turn));
        Assert.Equal(expected, picked.Value);
    }

    [Fact]
    public async Task FallsBackToFirstAvailableCardWithTheAbility()
    {
        using var caller = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));

        var creds = new FixedAiCredentials().Ready("google", "sk-google");
        var (os, sp, catalog, _) = await StartHost(
            topology: IntoChatOSNames.TopologyCloud,
            credentials: creds);

        await using var _os = os;
        await using var _sp = sp;

        var picked = await catalog.Pick(ModelRole.Embed);
        var card = KnownModels.Find(picked.Value);
        Assert.NotNull(card);
        Assert.Equal(Where.Cloud, card.Where);
        Assert.True(card.Can.HasFlag(Abilities.Embed));
    }

    [Fact]
    public async Task UnkeyedCardsAreListedWithAReason()
    {
        using var caller = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));

        var (os, sp, catalog, _) = await StartHost(topology: IntoChatOSNames.TopologyCloud);
        await using var _os = os;
        await using var _sp = sp;

        var availability = (await catalog.Availability()).Single(x => x.Model == KnownModels.Opus5.Id);
        Assert.False(availability.Ready);
        Assert.Equal(ModelCatalog.NoKeyReason, availability.Reason);
    }

    [Fact]
    public async Task ListFiltersByAbility()
    {
        using var caller = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));

        var (os, sp, catalog, _) = await StartHost();
        await using var _os = os;
        await using var _sp = sp;

        var hear = await catalog.List(Abilities.Hear);
        Assert.NotEmpty(hear);
        Assert.All(hear, c => Assert.True(c.Can.HasFlag(Abilities.Hear)));
    }

    [Fact]
    public async Task DetectedOllamaTagsGetCards()
    {
        using var caller = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));

        var runtime = new FakeRuntime(new ModelCard(
            new ModelId("llama3.2:3b"),
            AiProvider.Ollama,
            "llama3.2",
            "llama3.2:3b",
            Where.Device,
            Abilities.Text | Abilities.Tools,
            Tier.Fast));

        var (os, sp, catalog, _) = await StartHost(
            topology: IntoChatOSNames.TopologyLocal,
            runtimes: [runtime]);

        await using var _os = os;
        await using var _sp = sp;

        var cards = await catalog.List();
        Assert.Contains(cards, c => c.Id.Value == "llama3.2:3b"
            && c.Where == Where.Device
            && c.Provider == AiProvider.Ollama
            && c.Family == "llama3.2"
            && c.Name == "llama3.2:3b");
    }

    [Fact]
    public async Task AnAgentCannotSetARole()
    {
        var (os, sp, catalog, _) = await StartHost();
        await using var _os = os;
        await using var _sp = sp;

        using var agent = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Agent(new Actor.Person("local", "local"), new BrainId("local")));

        await Assert.ThrowsAsync<RuleViolationException>(() => catalog.SetRole(ModelRole.Chat, KnownModels.Gpt54.Id));
    }

    [Fact]
    public async Task SetRolePersistsAndReachesDecide()
    {
        using var caller = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));

        var creds = new FixedAiCredentials()
            .Ready("anthropic", "sk-anthropic")
            .Ready("openrouter", "sk-openrouter");

        var (os, sp, catalog, settings) = await StartHost(
            topology: IntoChatOSNames.TopologyCloud,
            configureAi: ai => ai.Roles.Cloud[ModelRole.Chat] = KnownModels.DeepSeekV41Flash.Id.Value,
            credentials: creds);

        await using var _os = os;
        await using var _sp = sp;

        await catalog.SetRole(ModelRole.Chat, KnownModels.Sonnet5.Id);

        var roles = await catalog.Roles();
        Assert.Equal(KnownModels.Sonnet5.Id, roles.Cloud[ModelRole.Chat]);
        Assert.Equal(KnownModels.Sonnet5.Id, (await settings.Read()).RoleOf(ModelRole.Chat));

        var resolved = await sp.GetRequiredService<ModelCatalog>().Decide("local", ModelRole.Chat, ct: TestContext.Current.CancellationToken);
        Assert.Equal((AiProvider.Anthropic, "claude-sonnet-5"), (resolved.Provider, resolved.Model));
    }

    [Fact]
    public async Task LocalCardSourceMergesCatalogCardAndPreservesBytes()
    {
        using var caller = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));

        const long sizeBytes = 2_500_000_000L;
        var runtime = new FakeRuntime(
            new ModelCard(KnownModels.Qwen35.Id, AiProvider.Ollama, "qwen", "raw-tag", Where.Device, Abilities.Text, Tier.Fast, Bytes: sizeBytes));

        var (os, sp, catalog, _) = await StartHost(
            topology: IntoChatOSNames.TopologyLocal,
            runtimes: [runtime]);

        await using var _os = os;
        await using var _sp = sp;

        var qwen = Assert.Single(await catalog.List(), c => c.Id == KnownModels.Qwen35.Id);
        Assert.Equal(KnownModels.Qwen35.Name, qwen.Name);
        Assert.Equal(KnownModels.Qwen35.Can, qwen.Can);
        Assert.Equal(sizeBytes, qwen.Bytes);
    }

    [Fact]
    public void StoredStringsStillRead()
    {
        var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "work-fast" };

        Assert.True(StoredModel.TryRead("preset:IQwen35", Empty, out var a));
        Assert.Equal("qwen3.5:4b", a.Model);

        Assert.True(StoredModel.TryRead("ollama/qwen3:14b", Empty, out var b));
        Assert.Equal("qwen3:14b", b.Model);

        Assert.True(StoredModel.TryRead("ollama:qwen2.5-coder:14b", Empty, out var c));
        Assert.Equal("qwen2.5-coder:14b", c.Model);

        Assert.True(StoredModel.TryRead("profile:WORK-FAST", profiles, out var d));
        Assert.Equal("work-fast", d.Model);

        Assert.False(StoredModel.TryRead("gpt-9-unknown", Empty, out _));
        Assert.False(StoredModel.TryRead("claude-sonnet-4-5", Empty, out _));
        Assert.False(StoredModel.TryRead("http://localhost:11434", Empty, out _));
    }
}
