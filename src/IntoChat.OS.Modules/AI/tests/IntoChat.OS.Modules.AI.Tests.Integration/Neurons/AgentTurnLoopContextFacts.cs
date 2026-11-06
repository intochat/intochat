using System.Text.Json;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using IntoChat.OS.Connections.Surface;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using DigitalBrain;
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
        Assert.Equal(new AgentTurnEvent.Failed("Blocked: halt now", false), events[^1]);
        Assert.Equal(2, events.Count);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task AMissingContextIsSkipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = StubChatClient.Replying("ok");
        await using var runtime = await Start(client, ct);

        var events = await TurnLoops.Collect(runtime.Services, "hello", [], ct, [runtime.Brain.Value + "/context/never-written"]);

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
            services.AddSingleton(new NeuronSurface(null!, null!, null!));
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
