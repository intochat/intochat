using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using IntoChat.OS.Connections.Surface;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

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
        await Write(runtime, "pick", new("pick", """{"model":"stub-b"}""", [], [ContextTags.Inference]));

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
            using var _ = RuntimeCallerContext.Stamp(CapabilityCallers.Platform("p1", "a1", runtime.Brain.Value));
            await runtime.Get<IAgentContext>(Id(runtime, "halt")).Set(new("stop", "halt now", [], [ContextTags.Interrupt]));
        }, ct);

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
        await Task.Run(async () =>
        {
            using var _ = RuntimeCallerContext.Stamp(new CapabilityCaller("p1", "a1", runtime.Brain.Value) { AppId = "notes" });
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

    private async Task<INeuronRuntime> Start(StubChatClient client, CancellationToken ct, Action<AIOptions>? configure = null)
        => await fixture.Start(NeuronRuntimeKind.Silo, ct, siloServices: services =>
        {
            services.AddSingleton<IChatClient>(client);
            // The loop resolves the surface for every turn but touches it only when a contract is composed; these turns compose none.
            services.AddSingleton(new OsNeuronSurface(null!, null!, null!));
            // No provider is provisioned, so an unpinned turn runs on the host client, which is the stub.
            services.AddSingleton<IAiCredentials, NoProviders>();
            services.AddOptions<AIOptions>();
            services.AddSingleton<ModelCatalog>();
            if (configure is not null) { services.Configure(configure); }
        });

    private static NeuronId Id(INeuronRuntime runtime, string name) => runtime.Id("agent-context", "context/" + name);

    private static string Key(INeuronRuntime runtime, string name) => ContextKeys.Of(runtime.Brain.Value, name);

    private static Task Write(INeuronRuntime runtime, string name, ContextEntry entry) => runtime.Get<IAgentContext>(Id(runtime, name)).Set(entry);

    private sealed class NoProviders : IAiCredentials
    {
        public Task<IntegrationSnapshot> StatusOf(string integrationId, CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationSnapshot { IntegrationId = integrationId, Status = IntegrationStatus.Unconfigured });

        public Task<long> GenerationOf(string integrationId, CancellationToken cancellationToken = default) => Task.FromResult(0L);

        public Task<ReleasedIntegration> MaterializeAsync(string integrationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> ReleaseSecretAsync(string integrationId, string field, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> BrainKey(string brainId, AiProvider provider, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
