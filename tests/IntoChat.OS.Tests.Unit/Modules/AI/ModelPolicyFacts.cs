using System.Net;
using System.Text;
using System.Xml.Linq;
using DigitalBrain;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Testing;
using IntoChat.OS.LocalSetup;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Models.Ollama;
using IntoChat.OS.Modules.AI.Pricing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.AI.Tests.Unit;

public sealed partial class ModelPolicyFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class StubProbe(LocalOllamaDetection? detection) : ILocalOllamaProbe
    {
        public Task<LocalOllamaDetection?> Detect(CancellationToken ct = default) => Task.FromResult(detection);
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

    [Fact]
    public void ModelRefParseRequiresExplicitGrammarAndRejectsGuesses()
    {
        var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "work-fast" };

        var cataloguedById = Assert.IsType<ModelRef.Catalogued>(ModelRef.Parse("qwen3.5:4b", profiles));
        Assert.Equal("qwen3.5:4b", cataloguedById.Model.Id);

        var cataloguedByMarker = Assert.IsType<ModelRef.Catalogued>(ModelRef.Parse("IQwen35", profiles));
        Assert.Equal(typeof(IQwen35), cataloguedByMarker.Model.Marker);

        var profile = Assert.IsType<ModelRef.Profile>(ModelRef.Parse("WORK-FAST", profiles));
        Assert.Equal("work-fast", profile.Name);

        var ollamaTag = Assert.IsType<ModelRef.Tag>(ModelRef.Parse("ollama/qwen3:14b", profiles));
        Assert.Equal((AiProvider.Ollama, "qwen3:14b"), (ollamaTag.Provider, ollamaTag.Id));

        var colonTag = Assert.IsType<ModelRef.Tag>(ModelRef.Parse("ollama:qwen2.5-coder:14b", profiles));
        Assert.Equal((AiProvider.Ollama, "qwen2.5-coder:14b"), (colonTag.Provider, colonTag.Id));

        var openRouterTag = Assert.IsType<ModelRef.Tag>(ModelRef.Parse("openrouter/deepseek/deepseek-chat", profiles));
        Assert.Equal((AiProvider.OpenRouter, "deepseek/deepseek-chat"), (openRouterTag.Provider, openRouterTag.Id));

        Assert.Throws<ArgumentException>(() => ModelRef.Parse("", profiles));
        Assert.Throws<ArgumentException>(() => ModelRef.Parse("   ", profiles));
        Assert.Throws<ArgumentException>(() => ModelRef.Parse("claude-sonnet-4-5", profiles));
        Assert.Throws<ArgumentException>(() => ModelRef.Parse("gpt-5", profiles));
        Assert.Throws<ArgumentException>(() => ModelRef.Parse("gemini-2.5-pro", profiles));
        Assert.Throws<ArgumentException>(() => ModelRef.Parse("totally-unknown-model", profiles));
        Assert.Throws<ArgumentException>(() => ModelRef.Parse("http://localhost:11434", profiles));
    }

    [Fact]
    public void ModelRefReadStoredReadsLegacyStringsAndRejectsUnknownGarbage()
    {
        var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "custom-profile" };

        var claude = Assert.IsType<ModelRef.Tag>(ModelRef.ReadStored("claude-sonnet-4-5", profiles));
        Assert.Equal((AiProvider.Anthropic, "claude-sonnet-4-5"), (claude.Provider, claude.Id));

        var gpt = Assert.IsType<ModelRef.Tag>(ModelRef.ReadStored("gpt-5", profiles));
        Assert.Equal((AiProvider.OpenAI, "gpt-5"), (gpt.Provider, gpt.Id));

        var gemma12b = Assert.IsType<ModelRef.Catalogued>(ModelRef.ReadStored("gemma4:12b", profiles));
        Assert.Equal("gemma4:12b", gemma12b.Model.Id);

        var gemmaLegacyTag = Assert.IsType<ModelRef.Tag>(ModelRef.ReadStored("gemma4:e2b", profiles));
        Assert.Equal((AiProvider.Ollama, "gemma4:e2b"), (gemmaLegacyTag.Provider, gemmaLegacyTag.Id));

        var coder = Assert.IsType<ModelRef.Tag>(ModelRef.ReadStored("qwen2.5-coder:14b", profiles, ["qwen2.5-coder:14b"]));
        Assert.Equal((AiProvider.Ollama, "qwen2.5-coder:14b"), (coder.Provider, coder.Id));

        Assert.Throws<ArgumentException>(() => ModelRef.ReadStored("not-a-valid-model-at-all", profiles));
    }

    [Fact]
    public async Task DecideRespectsPrecedenceTurnThenPersonThenDeploymentThenDetected()
    {
        var services = new ServiceCollection();
        services.AddOptions<AIOptions>().Configure(o => o.Default.Model = "claude-sonnet-4-6");
        services.AddSingleton<IAiCredentials>(new FixedAiCredentials()
            .Ready("openai", "sk-openai")
            .Ready("anthropic", "sk-anthropic")
            .Ready("google", "sk-google"));
        services.AddSingleton<IPriceList, ModelPriceList>();
        services.AddSingleton<ILocalOllamaProbe>(new StubProbe(new LocalOllamaDetection(
            "http://127.0.0.1:11434",
            "qwen3.5:4b",
            ["qwen3.5:4b", "qwen2.5-coder:14b"],
            HasNomicEmbed: false)));
        AIClients.Add(services);
        services.AddSingleton<ModelCatalog>();

        await using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<ModelCatalog>();

        // 1. Turn beats Person, Deployment, and Detected.
        var byTurn = await catalog.Decide("local", ModelUse.Chat, new ModelSelection("gpt-5.4"), personModel: "gemini-3.6-pro", ct: Ct);
        Assert.Equal((AiProvider.OpenAI, "gpt-5.4"), (byTurn.Provider, byTurn.Model));

        // 2. Person beats Deployment and Detected when Turn is omitted.
        var byPerson = await catalog.Decide("local", ModelUse.Chat, turn: null, personModel: "gemini-3.6-pro", ct: Ct);
        Assert.Equal((AiProvider.Google, "gemini-3.6-pro"), (byPerson.Provider, byPerson.Model));

        // 3. Deployment beats Detected when Turn and Person are omitted.
        var byDeployment = await catalog.Decide("local", ModelUse.Chat, turn: null, personModel: null, ct: Ct);
        Assert.Equal((AiProvider.Anthropic, "claude-sonnet-4-6"), (byDeployment.Provider, byDeployment.Model));
    }

    [Fact]
    public async Task CoderModelIsSelectedOnlyForCodeAndChatUsesChatDefault()
    {
        var services = new ServiceCollection();
        services.AddOptions<AIOptions>().Configure(o =>
        {
            o.Ollama.Endpoint = "http://127.0.0.1:11434";
            o.Default.Model = nameof(IQwen35);
        });
        services.AddSingleton<IAiCredentials>(new FixedAiCredentials());
        services.AddSingleton<IPriceList, ModelPriceList>();
        services.AddSingleton<ILocalOllamaProbe>(new StubProbe(new LocalOllamaDetection(
            "http://127.0.0.1:11434",
            "qwen3.5:4b",
            ["qwen2.5-coder:14b", "qwen3.5:4b", "nomic-embed-text:latest"],
            HasNomicEmbed: true)));
        AIClients.Add(services);
        services.AddSingleton<ModelCatalog>();

        await using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<ModelCatalog>();

        var chat = await catalog.Decide("local", ModelUse.Chat, ct: Ct);
        Assert.Equal((AiProvider.Ollama, "qwen3.5:4b"), (chat.Provider, chat.Model));

        var code = await catalog.Decide("local", ModelUse.Code, ct: Ct);
        Assert.Equal((AiProvider.Ollama, "qwen2.5-coder:14b"), (code.Provider, code.Model));

        // Even with zero explicit deployment default, detected chat picks non-coder over coder.
        Assert.Equal("qwen3:14b", HttpLocalOllamaProbe.SelectPreferredModel(["qwen2.5-coder:14b", "qwen3:14b"]));
        Assert.Equal("qwen3.5:4b", HttpLocalOllamaProbe.SelectPreferredModel(["qwen2.5-coder:14b", "qwen3.5:4b"]));
        Assert.Equal("qwen2.5-coder:14b", HttpLocalOllamaProbe.SelectPreferredCoderModel(["qwen3.5:4b", "qwen2.5-coder:14b"]));
    }

    private sealed class CapturingHttpClientFactory(Uri expectedTagsUri, string responseBody) : IHttpClientFactory
    {
        public Uri? RequestedUri { get; private set; }

        public HttpClient CreateClient(string name)
            => new(new CapturingHandler(uri => RequestedUri = uri, expectedTagsUri, responseBody));

        private sealed class CapturingHandler(Action<Uri?> onRequest, Uri expectedUri, string body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                onRequest(request.RequestUri);
                Assert.Equal(expectedUri, request.RequestUri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }
        }
    }

    [Fact]
    public async Task HttpLocalOllamaProbeUsesConfiguredEndpointInsteadOfHardcodedAddress()
    {
        var prev = Environment.GetEnvironmentVariable("INTOCHAT_DISABLE_LOCAL_OLLAMA_PROBE");
        Environment.SetEnvironmentVariable("INTOCHAT_DISABLE_LOCAL_OLLAMA_PROBE", null);
        const string configuredEndpoint = "http://127.0.0.1:41434";
        const string body = """{"models":[{"name":"qwen3.5:4b"},{"name":"qwen2.5-coder:14b"}]}""";
        var factory = new CapturingHttpClientFactory(new Uri($"{configuredEndpoint}/api/tags"), body);

        try
        {
            var services = new ServiceCollection();
            services.AddOptions<AIOptions>().Configure(o => o.Ollama.Endpoint = configuredEndpoint);
            services.AddSingleton<IHttpClientFactory>(factory);
            services.AddSingleton<TimeProvider>(TimeProvider.System);
            services.AddSingleton<HttpLocalOllamaProbe>();
            await using var sp = services.BuildServiceProvider();

            var probe = sp.GetRequiredService<HttpLocalOllamaProbe>();
            var detected = await probe.Detect(Ct);
            Assert.NotNull(detected);
            Assert.Equal(configuredEndpoint, detected.Endpoint);
            Assert.Equal("qwen3.5:4b", detected.PreferredModel);
            Assert.Equal(new Uri($"{configuredEndpoint}/api/tags"), factory.RequestedUri);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTOCHAT_DISABLE_LOCAL_OLLAMA_PROBE", prev);
        }
    }

    [Fact]
    public void DefaultChatClientFollowsUnifiedDefaultModelGrammar()
    {
        var services = new ServiceCollection();
        services.AddOptions<AIOptions>().Configure(o => o.Default.Model = "qwen3.5:4b");
        services.AddSingleton<IAiCredentials>(new FixedAiCredentials());
        services.AddSingleton<ILocalOllamaProbe>(new StubProbe(null));
        AIClients.Add(services);
        services.AddSingleton<ModelCatalog>();

        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<ModelCatalog>();
        var chatClient = provider.GetRequiredService<IChatClient>();

        Assert.NotNull(chatClient);
        Assert.Equal("qwen3.5:4b", ModelCatalog.DefaultChatModelId);
        Assert.Equal(LocalModelOptions.DefaultModel, ModelCatalog.DefaultChatModelId);
    }

    [Fact]
    public void DefaultChatModelLiteralExistsOnceInOwnedModelPolicyCode()
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
        var frameworkDir = Assert.Single(projectDirs, d => d.EndsWith("Modules\\Identity", StringComparison.OrdinalIgnoreCase) || d.EndsWith("Modules/Identity", StringComparison.OrdinalIgnoreCase));

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
            Path.Combine(appHostDir, "Hosting", "Composition", "DeploymentProfile.cs"),
            Path.Combine(appHostDir, "Hosting", "Modules", "AI"),
            localSetupDir,
            Path.Combine(hostDir, "Configuration"),
            Path.Combine(frameworkDir, "Visuals"),
        ];

        // Two homes, proven equal below: the hosting default (IntoChatOSNames) and the model's own identity in the AI contracts,
        // which may not depend on hosting code.
        string[] expectedSources =
        [
            Path.GetFullPath(Path.Combine(hostDir, "Configuration", "IntoChatOSNames.cs")),
            Path.GetFullPath(Path.Combine(aiDir, "Contracts", "Models", "Ollama", "IQwen35.cs")),
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

        Assert.Equal(expectedSources.Order(StringComparer.OrdinalIgnoreCase), literalHits.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheLocalDefaultChatModelIsTheQwen35Model()
        => Assert.Equal(IntoChat.OS.Host.Configuration.IntoChatOSNames.LocalDefaultChatModel, new IntoChat.OS.Modules.AI.Models.Ollama.Qwen35().Id);

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
