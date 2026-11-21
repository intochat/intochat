using IntoChat.OS.Tests;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Kernel;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Runs;
using IntoChat.OS.Modules.Mcp;
using IntoChat.OS.Modules.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Host.Surface;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

// The turn loop acts on context tags. The loop reaches a context by its os key through the silo's grain factory,
// so these facts run on the silo runtime only: TestOS has no grain factory for SiloNeurons to use.
public sealed class AgentTurnLoopContextFacts(AiFixture fixture) : IClassFixture<AiFixture>
{
    [Fact]
    public async Task UntaggedEntriesPrecedeTheTranscript()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = StubChatClient.Replying("ok");
        await using var runtime = await Start(client, ct);
        using var person = runtime.As(Who.Person);
        await Write(runtime, "facts-a", new("fact", "a fact", []));

        var events = await TurnLoops.Collect(runtime.Services, "hello", [], ct, [Key(runtime, "facts-a")], [new("user", [new AiText("earlier")])]);

        Assert.IsType<AgentTurnEvent.Completed>(events[^2]);
        var messages = Assert.Single(client.Requests);
        var fact = messages.ToList().FindIndex(message => message.Role == ChatRole.System && message.Text == "a fact");
        var firstUser = messages.ToList().FindIndex(message => message.Role == ChatRole.User);
        Assert.InRange(fact, 0, firstUser - 1);
        Assert.Equal("earlier", messages[firstUser].Text);
    }

    [Fact]
    public async Task ARelativeContextKeyReadsTheAgentsOwnBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = StubChatClient.Replying("ok");
        await using var runtime = await Start(client, ct);
        using var person = runtime.As(Who.Person);
        await Write(runtime, "relative", new("fact", "a relative fact", []));

        var events = await TurnLoops.Collect(runtime.Services, "hello", [], ct, ["context/relative"], conversation: runtime.Brain.Value + "/agents/loop");

        Assert.IsType<AgentTurnEvent.Completed>(events[^2]);
        Assert.Contains(Assert.Single(client.Requests), message => message.Role == ChatRole.System && message.Text == "a relative fact");
    }

    [Fact]
    public async Task AnInferenceEntrySetsTemperature()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Capturing(out var options);
        await using var runtime = await Start(client, ct);
        using var person = runtime.As(Who.Person);
        await Write(runtime, "tune", new("tune", """{"temperature":0.2}""", [], [ContextTags.Inference]));

        var events = await TurnLoops.Collect(runtime.Services, "hello", [], ct, [Key(runtime, "tune")]);

        Assert.IsType<AgentTurnEvent.Completed>(events[^2]);
        Assert.Equal(0.2f, options()!.Temperature);
    }

    [Fact]
    public async Task AnInferenceEntrySelectsTheModel()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        var received = server.ReplyOnce("application/x-ndjson",
            """{"model":"stub-b","created_at":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":"ok"},"done":true,"done_reason":"stop"}""" + "\n", ct);
        await using var runtime = await Start(StubChatClient.Replying("host"), ct, options => options.ModelProfiles["stub-b"] = new()
        { Provider = "Ollama", Model = "stub-b", Endpoint = server.Url });
        // The person picks the model.
        using var person = runtime.As(Who.Person);
        await Write(runtime, "pick", new("pick", """{"model":"stub-b"}""", [], [ContextTags.Inference]), Who.Person);

        var events = await TurnLoops.Collect(runtime.Services, "hello", [], ct, [Key(runtime, "pick")]);

        Assert.Equal("stub-b", Assert.Single(events.OfType<AgentTurnEvent.ModelSelected>()).Model.Model);
        Assert.IsType<AgentTurnEvent.Completed>(events[^2]);
        Assert.Contains("\"stub-b\"", (await received).Body);
    }

    [Fact]
    public async Task ATrustedInterruptBlocksBeforeAnyModelCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = StubChatClient.Replying("ok");
        await using var runtime = await Start(client, ct);
        await Task.Run(async () =>
        {
            using var _ = runtime.Edge.EnterKernel(runtime.Brain);
            await runtime.Get<IAgentContext>(Id(runtime, "halt")).Set(new("stop", "halt now", [], [ContextTags.Interrupt]));
        }, ct);
        using var person = runtime.As(Who.Person);

        var events = await TurnLoops.Collect(runtime.Services, "hello", [], ct, [Key(runtime, "halt")]);

        Assert.IsType<AgentTurnEvent.Started>(events[0]);
        Assert.Equal(new AgentTurnEvent.TurnFailed("Blocked: halt now", false), events[^1]);
        Assert.Equal(2, events.Count);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task AMissingContextIsSkipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = StubChatClient.Replying("ok");
        await using var runtime = await Start(client, ct);

        var events = await TurnLoops.Collect(runtime.Services, "hello", [], ct, [ContextKeys.Of(runtime.Brain.Value, "never-written")]);

        Assert.IsType<AgentTurnEvent.Completed>(events[^2]);
    }

    [Fact]
    public async Task MalformedInferenceJsonIsIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Capturing(out var options);
        await using var runtime = await Start(client, ct);
        await Write(runtime, "bad", new("bad", "{not json", [], [ContextTags.Inference]));

        var events = await TurnLoops.Collect(runtime.Services, "hello", [], ct, [Key(runtime, "bad")]);

        Assert.IsType<AgentTurnEvent.Completed>(events[^2]);
        Assert.Null(options()!.Temperature);
    }

    [Fact]
    public async Task AnInterruptWithoutUserOrPlatformTrustDoesNotBlock()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = StubChatClient.Replying("ok");
        await using var runtime = await Start(client, ct);
        using var person = runtime.As(Who.Person);
        await Task.Run(async () =>
        {
            using var _ = runtime.Edge.Enter(runtime.Edge.App("notes", new Actor.Person("p1", "a1"), runtime.Brain));
            await runtime.Get<IAgentContext>(Id(runtime, "app-halt")).Set(new("stop", "halt by app", [], [ContextTags.Interrupt]));
        }, ct);
        await Write(runtime, "plain-halt", new("stop", "halt unstamped", [], [ContextTags.Interrupt]));

        var events = await TurnLoops.Collect(runtime.Services, "hello", [], ct, [Key(runtime, "app-halt"), Key(runtime, "plain-halt")]);

        Assert.IsType<AgentTurnEvent.Completed>(events[^2]);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task TaggedEntriesNeverReachTheModel()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = StubChatClient.Replying("ok");
        await using var runtime = await Start(client, ct);
        using var person = runtime.As(Who.Person);
        await Write(runtime, "focus", new("focus", "focused on tab 3", [], [ContextTags.UiFocus]));
        await Write(runtime, "halt", new("stop", "untrusted halt", [], [ContextTags.Interrupt]));
        await Write(runtime, "plain", new("fact", "a plain fact", []));

        var events = await TurnLoops.Collect(runtime.Services, "hello", [], ct, [Key(runtime, "focus"), Key(runtime, "halt"), Key(runtime, "plain")]);

        Assert.IsType<AgentTurnEvent.Completed>(events[^2]);
        var system = Assert.Single(client.Requests).Where(message => message.Role == ChatRole.System).Select(message => message.Text);
        Assert.Equal(["a plain fact"], system);
    }

    [Fact]
    public async Task LaterInferenceEntriesWinInContextsOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Capturing(out var options);
        await using var runtime = await Start(client, ct);
        using var person = runtime.As(Who.Person);
        await Write(runtime, "low", new("low", """{"temperature":0.2}""", [], [ContextTags.Inference]));
        await Write(runtime, "high", new("high", """{"temperature":0.7}""", [], [ContextTags.Inference]));

        await TurnLoops.Collect(runtime.Services, "hello", [], ct, [Key(runtime, "low"), Key(runtime, "high")]);
        Assert.Equal(0.7f, options()!.Temperature);

        await TurnLoops.Collect(runtime.Services, "hello", [], ct, [Key(runtime, "high"), Key(runtime, "low")]);
        Assert.Equal(0.2f, options()!.Temperature);
    }

    [Fact]
    public async Task ModelResolutionHandlesCataloguedIdsAliasesAndMissingCredentialsDiagnostics()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        var qwenReply = server.ReplyOnce("application/x-ndjson",
            """{"model":"qwen3.5:4b","created_at":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":"from qwen"},"done":true,"done_reason":"stop"}""" + "\n", ct);
        await using var runtime = await Start(StubChatClient.Replying("host"), ct, options => options.Ollama.Endpoint = server.Url);
        using var person = runtime.As(Who.Person);
        var catalog = runtime.Services.GetRequiredService<ModelCatalog>();

        // 1. Resolve catalogued model by ID ("qwen3.5:4b") and marker ("IQwen35" / "Qwen35")
        var qwenById = await catalog.Resolve(new ModelSelection("qwen3.5:4b"), requiresTools: true, ct);
        var qwenByMarker = await catalog.Resolve(new ModelSelection("IQwen35"), requiresTools: true, ct);
        Assert.Equal((AiProvider.Ollama, "qwen3.5:4b"), (qwenById.Provider, qwenById.Model));
        Assert.Equal((AiProvider.Ollama, "qwen3.5:4b"), (qwenByMarker.Provider, qwenByMarker.Model));

        // 2. Execute turn loop with "qwen3.5:4b" in user.profile context and verify ModelSelected event
        await Write(runtime, "user.profile", new("inference", """{"model":"qwen3.5:4b"}""", [], [ContextTags.Inference]));
        var firstTurnEvents = await TurnLoops.Collect(runtime.Services, "hello qwen", [], ct, [Key(runtime, "user.profile")]);
        var selectedQwen = Assert.Single(firstTurnEvents.OfType<AgentTurnEvent.ModelSelected>());
        Assert.Equal((AiProvider.Ollama, "qwen3.5:4b"), (selectedQwen.Model.Provider, selectedQwen.Model.Model));
        Assert.IsType<AgentTurnEvent.Completed>(firstTurnEvents[^2]);
        Assert.Contains("\"qwen3.5:4b\"", (await qwenReply).Body);

        // 3. Switch model at runtime to an unconfigured cloud provider ("claude-sonnet-4-5") and verify graceful diagnostic failure
        await Write(runtime, "user.profile", new("inference", """{"model":"claude-sonnet-4-5"}""", [], [ContextTags.Inference]));
        var failedTurnEvents = await TurnLoops.Collect(runtime.Services, "hello claude", [], ct, [Key(runtime, "user.profile")]);
        var failed = Assert.Single(failedTurnEvents.OfType<AgentTurnEvent.TurnFailed>());
        Assert.False(failed.Cancelled);
        Assert.Contains("anthropic", failed.Message, StringComparison.OrdinalIgnoreCase);

        // 4. Switch model at runtime to another local Ollama tag ("gemma4:e2b") and verify ModelSelected updates immediately
        var gemmaReply = server.ReplyOnce("application/x-ndjson",
            """{"model":"gemma4:e2b","created_at":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":"from gemma"},"done":true,"done_reason":"stop"}""" + "\n", ct);
        await Write(runtime, "user.profile", new("inference", """{"model":"gemma4:e2b"}""", [], [ContextTags.Inference]));
        var thirdTurnEvents = await TurnLoops.Collect(runtime.Services, "hello gemma", [], ct, [Key(runtime, "user.profile")]);
        var selectedGemma = Assert.Single(thirdTurnEvents.OfType<AgentTurnEvent.ModelSelected>());
        Assert.Equal((AiProvider.Ollama, "gemma4:e2b"), (selectedGemma.Model.Provider, selectedGemma.Model.Model));
        Assert.IsType<AgentTurnEvent.Completed>(thirdTurnEvents[^2]);
        Assert.Contains("\"gemma4:e2b\"", (await gemmaReply).Body);
    }

    [Fact]
    public async Task NoContractsMeansToolModeNone()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Capturing(out var options);
        await using var runtime = await Start(client, ct);

        var events = await TurnLoops.Collect(runtime.Services, "hello", [], ct);

        Assert.IsType<AgentTurnEvent.Completed>(events[^2]);
        Assert.Empty(options()!.Tools!);
        Assert.Equal(ChatToolMode.None, options()!.ToolMode);
    }

    [Fact]
    public async Task TheModelSeesTypedToolsAndNoNeuronsCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Capturing(out var options);
        var compiled = new CompiledTypes([typeof(IFocus).Assembly]);
        var observer = new StubBrainObserver(compiled.Entries);
        var surface = new OsNeuronSurface(null!, observer, new NeuronTypeSearch(observer, [], null));
        await using var runtime = await Start(client, ct, surface: surface);
        using var person = runtime.As(Who.Person);

        var events = await TurnLoops.Collect(runtime.Services, "point me at settings", [typeof(IFocus).FullName!], ct);

        Assert.IsType<AgentTurnEvent.Completed>(events[^2]);
        var toolNames = options()!.Tools!.OfType<AIFunction>().Select(static t => t.Name).ToArray();
        Assert.Contains("focus_point", toolNames);
        Assert.Contains("neurons_search", toolNames);
        Assert.DoesNotContain("neurons_call", toolNames);
        Assert.DoesNotContain("neurons_get", toolNames);
        Assert.Equal(ChatToolMode.Auto, options()!.ToolMode);
    }

    [Fact]
    public async Task ASearchHitWidensTheNextStepsTools()
    {
        var ct = TestContext.Current.CancellationToken;
        var seenToolsPerStep = new List<string[]>();
        var step = 0;
        var client = new StubChatClient((_, chat, _) =>
        {
            seenToolsPerStep.Add([.. chat?.Tools?.OfType<AIFunction>().Select(static t => t.Name) ?? []]);
            if (step++ == 0)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent("call-search", "neurons_search", new Dictionary<string, object?> { ["query"] = "files" })])));
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        });

        var compiled = new CompiledTypes([typeof(IFocus).Assembly, typeof(IntoChat.OS.Modules.AI.Memory.IMemory).Assembly]);
        var observer = new StubBrainObserver(compiled.Entries);
        var surface = new OsNeuronSurface(null!, observer, new NeuronTypeSearch(observer, [], null));
        await using var runtime = await Start(client, ct, surface: surface);
        using var person = runtime.As(Who.Person);

        // Compose enough contracts (> 16 tools) so routing activates and drops unrelated contracts on step 0,
        // then search for "files" so step 1 widens to include IFiles tools.
        string[] contracts =
        [
            typeof(IStudio).FullName!,
            typeof(IFlows).FullName!,
            typeof(ISynapseStudio).FullName!,
            typeof(IUserSettings).FullName!,
            typeof(IRuns).FullName!,
            typeof(IFocus).FullName!,
            typeof(IFiles).FullName!,
        ];

        var events = await TurnLoops.Collect(runtime.Services, "inspect studio pipeline flows and engineer runs", contracts, ct);

        Assert.IsType<AgentTurnEvent.Completed>(events[^2]);
        Assert.Equal(2, seenToolsPerStep.Count);
        Assert.DoesNotContain("files_write", seenToolsPerStep[0]);
        Assert.Contains("files_write", seenToolsPerStep[1]);
        Assert.Contains("files_open", seenToolsPerStep[1]);
    }

    [Fact]
    public async Task InoSeesWebToolsAndLearnTools()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Capturing(out var options);
        await using var mcpEndpoint = await MiniLearnMcpServer.StartAsync(ct);
        await using var webMcpFixture = new WebMcpTurnFixture([typeof(AIModule), typeof(IntoChat.OS.Modules.Identity.IdentityModule), typeof(McpModule), typeof(WebModule)]);

        var compiled = new CompiledTypes([typeof(IWebSearch).Assembly, typeof(IMcpServer).Assembly, typeof(IFocus).Assembly]);
        var instances = new List<BrainInstance>();
        var observer = new StubBrainObserver(compiled.Entries, instances);

        await using var runtime = await webMcpFixture.Start(NeuronRuntimeKind.Silo, ct, siloServices: services =>
        {
            services.AddSingleton<IChatClient>(client);
            services.AddSingleton(sp => new OsNeuronSurface(null!, observer, new NeuronTypeSearch(observer, [], null), plain: sp));
            services.AddSingleton<IAiCredentials, NoProviders>();
            services.AddOptions<AIOptions>();
            services.AddSingleton<ModelCatalog>();
        });

        using var person = runtime.As(Who.Person);
        var learnKey = $"{runtime.Brain.Value}/mcp/microsoft-learn";
        await runtime.Get<IMcpServer>(new NeuronId(runtime.Brain, "mcp.server", "mcp/microsoft-learn"))
            .Connect(new McpServerSpec("Microsoft Learn", mcpEndpoint.Url), ct);
        instances.Add(new BrainInstance("mcp.server", learnKey, DateTimeOffset.UtcNow, "owner"));

        string[] contracts =
        [
            typeof(IWebSearch).FullName!,
            typeof(IWebRead).FullName!,
            typeof(IMcpServer).FullName!,
        ];

        var events = await TurnLoops.Collect(
            runtime.Services,
            "search the web for .NET 10 AOT and check Microsoft Learn docs",
            contracts,
            ct,
            conversation: runtime.Brain.Value + "/ino/conversations/c1");

        Assert.IsType<AgentTurnEvent.Completed>(events[^2]);
        var toolNames = options()!.Tools!.OfType<AIFunction>().Select(static t => t.Name).ToArray();
        Assert.Contains("web_search", toolNames);
        Assert.Contains("web_read", toolNames);
        Assert.Contains("microsoft_learn_microsoft_docs_search", toolNames);
    }

    [Fact]
    public async Task NativeSearchReplacesTheSearchTool()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new LoopbackServer();
        const string anthropicOk = """
            {
              "id": "msg_native",
              "type": "message",
              "role": "assistant",
              "model": "claude-sonnet-4-5",
              "content": [{ "type": "text", "text": "ok" }],
              "stop_reason": "end_turn",
              "usage": { "input_tokens": 1, "output_tokens": 1 }
            }
            """;

        var mode = new StubWebSearchMode { Provider = SearchProvider.Native };
        var compiled = new CompiledTypes([typeof(IWebSearch).Assembly]);
        var observer = new StubBrainObserver(compiled.Entries);

        await using var nativeFixture = new WebMcpTurnFixture([typeof(AIModule), typeof(IntoChat.OS.Modules.Identity.IdentityModule), typeof(StubWebSearchModule)]);
        await using var runtime = await nativeFixture.Start(NeuronRuntimeKind.Silo, ct, siloServices: services =>
        {
            services.AddSingleton(mode);
            services.AddSingleton(sp => new OsNeuronSurface(null!, observer, new NeuronTypeSearch(observer, [], null), plain: sp));
            services.AddSingleton<IAiCredentials>(new FakeAnthropicCredentials());
            services.AddOptions<AIOptions>();
            services.Configure<AIOptions>(o => o.ModelProfiles["claude-native"] = new()
            {
                Provider = "Anthropic",
                Model = "claude-sonnet-4-5",
                Endpoint = server.Url,
                Capabilities = LlmCapabilities.Tools,
            });
            services.AddSingleton<ModelCatalog>();
        });

        using var person = runtime.As(Who.Person);
        await Write(runtime, "pick-claude", new("pick", """{"model":"claude-native"}""", [], [ContextTags.Inference]), Who.Person);

        string[] contracts = [typeof(IWebSearch).FullName!, typeof(IWebRead).FullName!];

        var nativeRequest = server.ReplyOnce("application/json", anthropicOk, ct);
        var nativeEvents = await TurnLoops.Collect(
            runtime.Services,
            "search the web for .NET 10 AOT",
            contracts,
            ct,
            contexts: [Key(runtime, "pick-claude")],
            conversation: runtime.Brain.Value + "/ino/conversations/c1");
        Assert.IsType<AgentTurnEvent.Completed>(nativeEvents[^2]);

        using (var doc = JsonDocument.Parse((await nativeRequest).Body))
        {
            var tools = doc.RootElement.GetProperty("tools").EnumerateArray().ToArray();
            Assert.Contains(tools, static t => t.TryGetProperty("type", out var type) && (type.GetString()?.StartsWith("web_search", StringComparison.Ordinal) ?? false));
            Assert.DoesNotContain(tools, static t => t.TryGetProperty("name", out var name) && name.GetString() == "web_search"
                && (!t.TryGetProperty("type", out var type) || !(type.GetString()?.StartsWith("web_search", StringComparison.Ordinal) ?? false)));
            Assert.Contains(tools, static t => t.TryGetProperty("name", out var name) && name.GetString() == "web_read");
        }

        mode.Provider = SearchProvider.Brave;
        var braveRequest = server.ReplyOnce("application/json", anthropicOk, ct);
        var braveEvents = await TurnLoops.Collect(
            runtime.Services,
            "search the web for .NET 10 AOT",
            contracts,
            ct,
            contexts: [Key(runtime, "pick-claude")],
            conversation: runtime.Brain.Value + "/ino/conversations/c2");
        Assert.IsType<AgentTurnEvent.Completed>(braveEvents[^2]);

        using (var doc = JsonDocument.Parse((await braveRequest).Body))
        {
            var tools = doc.RootElement.GetProperty("tools").EnumerateArray().ToArray();
            Assert.DoesNotContain(tools, static t => t.TryGetProperty("type", out var type) && (type.GetString()?.StartsWith("web_search", StringComparison.Ordinal) ?? false));
            Assert.Contains(tools, static t => t.TryGetProperty("name", out var name) && name.GetString() == "web_search");
        }
    }

    // A stub that answers "ok" and keeps the options of its last request.
    private static StubChatClient Capturing(out Func<ChatOptions?> options)
    {
        ChatOptions? seen = null;
        options = () => seen;
        return new((_, chat, _) =>
        {
            seen = chat;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        });
    }

    private async Task<INeuronRuntime> Start(StubChatClient client, CancellationToken ct, Action<AIOptions>? configure = null, OsNeuronSurface? surface = null)
        => await fixture.Start(NeuronRuntimeKind.Silo, ct, siloServices: services =>
        {
            services.AddSingleton<IChatClient>(client);
            // The loop resolves the surface for every turn but touches it only when a contract is composed; these turns compose none.
            services.AddSingleton(surface ?? new OsNeuronSurface(null!, null!, null!));
            // No provider is provisioned, so an unpinned turn runs on the host client, which is the stub.
            services.AddSingleton<IAiCredentials, NoProviders>();
            services.AddOptions<AIOptions>();
            services.AddSingleton<ModelCatalog>();
            if (configure is not null) { services.Configure(configure); }
        });

    private static NeuronId Id(INeuronRuntime runtime, string name) => runtime.Id("agent-context", "context/" + name);

    private static string Key(INeuronRuntime runtime, string name) => ContextKeys.Of(runtime.Brain.Value, name);

    // An untrusted writer: an app acting for the person (an unstamped write used to read as app trust; task 1 refuses it now).
    private static Task Write(INeuronRuntime runtime, string name, ContextEntry entry, Who who = Who.App) => Task.Run(async () =>
    {
        using var app = runtime.As(who, "p1", "a1", app: "notes");
        await runtime.Get<IAgentContext>(Id(runtime, name)).Set(entry);
    });

    private sealed class NoProviders : IAiCredentials
    {
        public Task<IntegrationSnapshot> StatusOf(string integrationId, CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationSnapshot { IntegrationId = integrationId, Status = IntegrationStatus.Unconfigured });

        public Task<long> GenerationOf(string integrationId, CancellationToken cancellationToken = default) => Task.FromResult(0L);

        public Task<ReleasedIntegration> MaterializeAsync(string integrationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> ReleaseSecretAsync(string integrationId, string field, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> BrainKey(string brainId, AiProvider provider, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class StubBrainObserver(BrainType[] types, IReadOnlyList<BrainInstance>? instances = null) : IBrainObserver
    {
        public IAsyncEnumerable<BrainInstance> Instances<T>(CancellationToken cancellationToken) where T : class
            => Instances(null, cancellationToken);

        public async IAsyncEnumerable<BrainInstance> Instances(string? type, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (instances is not null)
            {
                foreach (var entry in instances)
                {
                    if (type is null || string.Equals(entry.Type, type, StringComparison.Ordinal))
                    {
                        yield return entry;
                    }
                }
            }

            await Task.CompletedTask;
        }

        public Task<BrainType[]> Types(CancellationToken cancellationToken = default)
            => Task.FromResult(types);

        public Task<string[]> Brains(CancellationToken cancellationToken = default)
            => Task.FromResult<string[]>([]);

        public Task<ObservedWire[]> Wires(CancellationToken cancellationToken = default)
            => Task.FromResult<ObservedWire[]>([]);

        public IAsyncEnumerable<SignalDelivery> Deliveries(CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<SignalDelivery>();

        public Task<JsonElement?> State(NeuronId id, CancellationToken cancellationToken = default)
            => Task.FromResult<JsonElement?>(null);
    }

    private sealed class WebMcpTurnFixture(IReadOnlyList<Type> modules) : BothRuntimesFixture
    {
        protected override IReadOnlyList<Type> Modules => modules;

        protected override void ConfigureSilo(IServiceCollection services)
        {
            services.AddJournalPayloads(typeof(IAgent).Assembly);
            services.AddSingleton<DigitalBrain.Capabilities.Secrets.ISecretRedactor, SilentRedactor>();
        }

        private sealed class SilentRedactor : DigitalBrain.Capabilities.Secrets.ISecretRedactor
        {
            public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken) => ValueTask.FromResult(text);
            public ValueTask<int> TailLength(CancellationToken cancellationToken) => ValueTask.FromResult(0);
        }
    }

    private sealed class FakeAnthropicCredentials : IAiCredentials
    {
        public Task<IntegrationSnapshot> StatusOf(string integrationId, CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationSnapshot
            {
                IntegrationId = integrationId,
                Status = string.Equals(integrationId, "anthropic", StringComparison.OrdinalIgnoreCase)
                    ? IntegrationStatus.Ready
                    : IntegrationStatus.Unconfigured,
            });

        public Task<long> GenerationOf(string integrationId, CancellationToken cancellationToken = default) => Task.FromResult(1L);

        public Task<ReleasedIntegration> MaterializeAsync(string integrationId, CancellationToken cancellationToken = default)
            => Task.FromResult(new ReleasedIntegration
            {
                Snapshot = new IntegrationSnapshot { IntegrationId = integrationId, Status = IntegrationStatus.Ready },
                Values = new Dictionary<string, string>(StringComparer.Ordinal) { [AiIntegrations.ApiKeyField] = "sk-ant-test" },
            });

        public Task<string> ReleaseSecretAsync(string integrationId, string field, CancellationToken cancellationToken = default)
            => Task.FromResult("sk-ant-test");

        public Task<string?> BrainKey(string brainId, AiProvider provider, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(provider == AiProvider.Anthropic ? "sk-ant-test" : null);
    }

    private sealed class MiniLearnMcpServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string Url => _app.Urls.Single() + "/mcp";

        private MiniLearnMcpServer(WebApplication app) => _app = app;

        public static async Task<MiniLearnMcpServer> StartAsync(CancellationToken ct)
        {
            var builder = WebApplication.CreateBuilder();
            var app = builder.Build();
            app.Urls.Add("http://127.0.0.1:0");
            app.MapPost("/mcp", async context =>
            {
                using var request = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                var root = request.RootElement;
                if (!root.TryGetProperty("id", out var id))
                {
                    context.Response.StatusCode = StatusCodes.Status202Accepted;
                    return;
                }

                var method = root.GetProperty("method").GetString();
                if (method is not ("initialize" or "tools/list"))
                {
                    context.Response.ContentType = "text/event-stream";
                    await context.Response.WriteAsync(
                        "event: message\ndata: " + JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "method not found" } }) + "\n\n",
                        context.RequestAborted);
                    return;
                }

                object result = method switch
                {
                    "initialize" => new
                    {
                        protocolVersion = root.GetProperty("params").GetProperty("protocolVersion").GetString(),
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "mini-learn", version = "1" },
                    },
                    "tools/list" => new
                    {
                        tools = new[]
                        {
                            new
                            {
                                name = "microsoft_docs_search",
                                description = "Search Microsoft Learn and .NET documentation.",
                                inputSchema = new { type = "object", properties = new { query = new { type = "string" } } },
                            },
                        },
                    },
                    _ => throw new InvalidOperationException(method),
                };

                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync(
                    "event: message\ndata: " + JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }) + "\n\n",
                    context.RequestAborted);
            });
            app.MapDelete("/mcp", () => Results.Ok());
            await app.StartAsync(ct);
            return new MiniLearnMcpServer(app);
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }
}

internal sealed class StubWebSearchMode
{
    public SearchProvider Provider { get; set; } = SearchProvider.Off;
}

[DigitalBrain.Modules.Module("test-web-search")]
internal sealed class StubWebSearchModule : DigitalBrain.Modules.Module
{
    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new([], [], [], [], []);
}

[DigitalBrain.Neurons.Neuron("web.search"), DigitalBrain.Neurons.StatelessNeuron]
internal sealed class StubNativeWebSearchNeuron(StubWebSearchMode? mode = null) : DigitalBrain.Neurons.Neuron, IWebSearch
{
    public Task<WebResult[]> Search(string query, int count = 8) => Task.FromResult<WebResult[]>([]);
    public Task<SearchProvider> Provider() => Task.FromResult(mode?.Provider ?? SearchProvider.Off);
}
