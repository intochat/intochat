using System.Runtime.CompilerServices;
using System.Xml.Linq;
using DigitalBrain;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.LocalSetup;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Pricing;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Devices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.AI.Tests.Unit;

public sealed partial class ModelPolicyFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeRuntime(bool reachable, params ModelCard[] installed) : ILocalRuntime
    {
        public RuntimeId Id => RuntimeId.Ollama;

        public Task<RuntimeStatus> Probe(CancellationToken ct)
            => Task.FromResult(new RuntimeStatus(Id, reachable, reachable ? "0.9.0" : null, new Uri("http://127.0.0.1:11434")));

        public async IAsyncEnumerable<ModelCard> Catalog([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var card in installed) { yield return card; }
        }

        public async IAsyncEnumerable<ModelCard> Installed([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            if (!reachable) { yield break; }
            foreach (var card in installed) { yield return card; }
        }

        public async IAsyncEnumerable<InstallProgress> Install(ModelId id, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task Remove(ModelId id, CancellationToken ct) => Task.CompletedTask;
        public IChatClient Chat(ModelId id) => new CapturingChatClient();
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class TestOSProxies(TestOS os) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class => os.Get<T>(id);
        public object Get(Type contract, NeuronId id) => throw new NotSupportedException();
    }

    [Fact]
    public void StoredModelTryReadAcceptsCatalogProfilesAndExplicitOllamaTagsAndRejectsGuesses()
    {
        var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "work-fast" };

        Assert.True(StoredModel.TryRead("qwen3.5:4b", profiles, out var cataloguedById));
        Assert.Equal("qwen3.5:4b", cataloguedById.Model);

        Assert.True(StoredModel.TryRead("IQwen35", profiles, out var cataloguedByMarker));
        Assert.Equal("qwen3.5:4b", cataloguedByMarker.Model);

        Assert.True(StoredModel.TryRead("preset:IQwen35", profiles, out var presetMarker));
        Assert.Equal("qwen3.5:4b", presetMarker.Model);

        Assert.True(StoredModel.TryRead("WORK-FAST", profiles, out var profile));
        Assert.Equal("work-fast", profile.Model);

        Assert.True(StoredModel.TryRead("ollama/qwen3:14b", profiles, out var ollamaTag));
        Assert.Equal("qwen3:14b", ollamaTag.Model);

        Assert.True(StoredModel.TryRead("ollama:qwen2.5-coder:14b", profiles, out var colonTag));
        Assert.Equal("qwen2.5-coder:14b", colonTag.Model);

        Assert.True(StoredModel.TryRead("gemma4:12b", profiles, out var gemmaAlias));
        Assert.Equal("gemma4:e2b", gemmaAlias.Model);

        Assert.False(StoredModel.TryRead("", profiles, out _));
        Assert.False(StoredModel.TryRead("   ", profiles, out _));
        Assert.False(StoredModel.TryRead("claude-sonnet-4-5", profiles, out _));
        Assert.False(StoredModel.TryRead("gpt-5", profiles, out _));
        Assert.False(StoredModel.TryRead("gemini-2.5-pro", profiles, out _));
        Assert.False(StoredModel.TryRead("totally-unknown-model", profiles, out _));
        Assert.False(StoredModel.TryRead("http://localhost:11434", profiles, out _));
    }

    [Fact]
    public async Task DecideRespectsPrecedenceTurnThenPersonThenDeployment()
    {
        using var caller = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));

        await using var os = TestOS.Local();
        os.Register<UserSettingsNeuron>();

        var services = new ServiceCollection();
        services.AddOptions<AIOptions>().Configure(o =>
        {
            o.Roles.Device[Role.Chat] = "claude-sonnet-4-6";
            o.Roles.Cloud[Role.Chat] = "claude-sonnet-4-6";
        });
        services.AddSingleton<IAiCredentials>(new FixedAiCredentials()
            .Ready("openai", "sk-openai")
            .Ready("anthropic", "sk-anthropic")
            .Ready("google", "sk-google"));
        services.AddSingleton<IPriceList, ModelPriceList>();
        services.AddSingleton<ILocalRuntime>(new FakeRuntime(true, KnownModels.Qwen35, KnownModels.Qwen25Coder14B));
        services.AddSingleton<INeuronProxies>(new TestOSProxies(os));
        AIClients.Add(services);
        services.AddSingleton<ModelCatalog>();

        await using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<ModelCatalog>();
        var settings = os.Get<IUserSettings>(new NeuronId(new BrainId("local"), "os.settings", "settings"));

        // 3. Deployment beats Detected when Turn and Person are omitted.
        var byDeployment = await catalog.Decide("local", Role.Chat, turn: null, ct: Ct);
        Assert.Equal((AiProvider.Anthropic, "claude-sonnet-4-6"), (byDeployment.Provider, byDeployment.Model));

        // 2. Person beats Deployment and Detected when Turn is omitted.
        await settings.SetRole(Role.Chat, KnownModels.Gemini36Pro.Id);
        var byPerson = await catalog.Decide("local", Role.Chat, turn: null, ct: Ct);
        Assert.Equal((AiProvider.Google, "gemini-3.6-pro"), (byPerson.Provider, byPerson.Model));

        // 1. Turn beats Person, Deployment, and Detected.
        var byTurn = await catalog.Decide("local", Role.Chat, new ModelSelection("gpt-5.4"), ct: Ct);
        Assert.Equal((AiProvider.OpenAI, "gpt-5.4"), (byTurn.Provider, byTurn.Model));
    }

    [Fact]
    public async Task CoderModelIsSelectedOnlyForThinkAndChatUsesChatDefault()
    {
        var services = new ServiceCollection();
        services.AddOptions<AIOptions>().Configure(o =>
        {
            o.Ollama.Endpoint = "http://127.0.0.1:11434";
            o.Roles.Device[Role.Chat] = KnownModels.Qwen35.Id.Value;
        });
        services.AddSingleton<IAiCredentials>(new FixedAiCredentials());
        services.AddSingleton<IPriceList, ModelPriceList>();
        services.AddSingleton<ILocalRuntime>(new FakeRuntime(true, KnownModels.Qwen25Coder14B, KnownModels.Qwen35));
        AIClients.Add(services);
        services.AddSingleton<ModelCatalog>();

        await using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<ModelCatalog>();

        var chat = await catalog.Decide("local", Role.Chat, ct: Ct);
        Assert.Equal((AiProvider.Ollama, "qwen3.5:4b"), (chat.Provider, chat.Model));

        var code = await catalog.Decide("local", Role.Think, ct: Ct);
        Assert.Equal((AiProvider.Ollama, "qwen2.5-coder:14b"), (code.Provider, code.Model));
    }

    [Fact]
    public void DefaultChatClientFollowsUnifiedDefaultModelGrammar()
    {
        var services = new ServiceCollection();
        services.AddOptions<AIOptions>().Configure(o => o.Roles.Device[Role.Chat] = KnownModels.Qwen35.Id.Value);
        services.AddSingleton<IAiCredentials>(new FixedAiCredentials());
        services.AddSingleton<ILocalRuntime>(new FakeRuntime(false));
        AIClients.Add(services);
        services.AddSingleton<ModelCatalog>();

        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<ModelCatalog>();
        var chatClient = provider.GetRequiredService<IChatClient>();

        Assert.NotNull(chatClient);
        Assert.Equal("qwen3.5:4b", KnownModels.Defaults.Device[Role.Chat].Value);
        Assert.Equal(LocalModelOptions.DefaultModel, KnownModels.Defaults.Device[Role.Chat].Value);
    }

    [Fact]
    public void DefaultChatModelLiteralExistsOnlyInModelsJson()
    {
        var slnPath = FindSolutionFile();
        var slnRoot = Path.GetDirectoryName(slnPath)!;
        var projectDirs = XDocument.Load(slnPath)
            .Descendants("Project")
            .Select(p => p.Attribute("Path")?.Value)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetDirectoryName(Path.GetFullPath(Path.Combine(slnRoot, p!)))!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var aiDir = Assert.Single(projectDirs, d => d.EndsWith("Modules\\AI", StringComparison.OrdinalIgnoreCase) || d.EndsWith("Modules/AI", StringComparison.OrdinalIgnoreCase));
        var appsDir = Assert.Single(projectDirs, d => d.EndsWith("IntoChat.OS.Apps", StringComparison.OrdinalIgnoreCase));
        var catalogDir = Assert.Single(projectDirs, d => d.EndsWith("Modules\\Catalog", StringComparison.OrdinalIgnoreCase) || d.EndsWith("Modules/Catalog", StringComparison.OrdinalIgnoreCase));
        var appHostDir = Assert.Single(projectDirs, d => d.EndsWith("IntoChat.OS.AppHost", StringComparison.OrdinalIgnoreCase));
        var localSetupDir = Assert.Single(projectDirs, d => d.EndsWith("IntoChat.OS.LocalSetup", StringComparison.OrdinalIgnoreCase));
        var hostDir = Assert.Single(projectDirs, d => d.EndsWith("IntoChat.OS.Host", StringComparison.OrdinalIgnoreCase));

        string[] ownedDirectories =
        [
            Path.Combine(aiDir, "Models"),
            Path.Combine(aiDir, "Contracts", "Models"),
            Path.Combine(aiDir, "Clients"),
            Path.Combine(appsDir, "Ino"),
            Path.Combine(appsDir, "Settings"),
            Path.Combine(catalogDir, "Ino"),
            Path.Combine(catalogDir, "Authoring"),
            Path.Combine(catalogDir, "Contracts", "System"),
            Path.Combine(catalogDir, "System"),
            Path.Combine(catalogDir, "Flows", "LocalLlmCoder.cs"),
            Path.Combine(appHostDir, "AppHost.cs"),
            Path.Combine(appHostDir, "Hosting", "Composition", "AIDeployment.cs"),
            Path.Combine(appHostDir, "Hosting", "Composition", "DeploymentProfile.cs"),
            Path.Combine(appHostDir, "Hosting", "Modules", "AI"),
            localSetupDir,
            Path.Combine(hostDir, "Configuration"),
            Path.Combine(hostDir, "Visuals"),
        ];

        var literalHits = new List<string>();
        foreach (var path in ownedDirectories)
        {
            var files = File.Exists(path) ? [path] : Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                if (File.ReadLines(file).Any(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                    && line.Contains("\"qwen3.5:4b\"", StringComparison.Ordinal)))
                {
                    literalHits.Add(Path.GetFullPath(file));
                }
            }
        }

        Assert.Empty(literalHits);
    }

    [Fact]
    public void TheLocalDefaultChatModelIsTheQwen35Card()
        => Assert.Equal(KnownModels.Qwen35.Id, KnownModels.Defaults.Device[Role.Chat]);

    private static string FindSolutionFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var slnx = Path.Combine(dir.FullName, "Intochat.OS.slnx");
            if (File.Exists(slnx))
            {
                return slnx;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate Intochat.OS.slnx above " + AppContext.BaseDirectory);
    }
}
