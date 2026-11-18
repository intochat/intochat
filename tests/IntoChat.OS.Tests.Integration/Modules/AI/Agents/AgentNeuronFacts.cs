using IntoChat.OS.Tests;
using IntoChat.OS.Framework;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using IntoChat.OS.Connections.Platform.Secrets;
using IntoChat.OS.Connections.Surface;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

// A run names the flow that started it and the trace it ran under, so a flow and a request can find their turns.
public sealed class AgentNeuronFacts(AiFixture fixture) : IClassFixture<AiFixture>
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task StartWithFlowIdStoresItOnTheRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await Start(StubChatClient.Replying("ok"), ct);
        using var person = runtime.As(Who.Person);
        var id = runtime.Id("agent", "agents/flow");
        var agent = runtime.Get<IAgent>(id);

        var runId = await agent.Start(Turn("hello"), "flow-42");
        await runtime.WaitForSignals<TurnCompleted>(id, 1, Within, ct);

        var run = Assert.Single((await agent.Read()).Runs, run => run.RunId == runId);
        Assert.Equal("flow-42", run.FlowId);
    }

    [Fact]
    public async Task TurnStartedCarriesTraceId()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await Start(StubChatClient.Replying("ok"), ct);
        using var person = runtime.As(Who.Person);
        var id = runtime.Id("agent", "agents/trace");
        var agent = runtime.Get<IAgent>(id);
        var trace = TraceContext.Begin("account-1", runtime.Brain.Value, id);

        string runId;
        using (TraceContext.Enter(trace)) { runId = await agent.Start(Turn("hello")); }
        var started = Assert.Single(await runtime.WaitForSignals<TurnStarted>(id, 1, Within, ct));
        await runtime.WaitForSignals<TurnCompleted>(id, 1, Within, ct);

        Assert.Equal(trace.TraceId, started.TraceId);
        Assert.Equal(trace.TraceId, Assert.Single((await agent.Read()).Runs, run => run.RunId == runId).TraceId);
    }

    [Fact]
    public async Task RuntimeModelSwitchAcrossTurnsUpdatesModelSelectedAndAgentRuns()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await Start(StubChatClient.Replying("ok"), ct);
        using var person = runtime.As(Who.Person);
        var id = runtime.Id("agent", "agents/model-switch");
        var agent = runtime.Get<IAgent>(id);
        var profileContext = runtime.Get<IAgentContext>(runtime.Id("agent-context", "context/user.profile"));

        // 1. Configure agent with initial model ("qwen3.5:4b") and run first turn
        await agent.Configure(new AgentDefinition
        {
            Model = new ModelSelection("qwen3.5:4b"),
            Contexts = ["context/user.profile"],
        }, 0);
        await profileContext.Set(new ContextEntry("inference", """{"model":"qwen3.5:4b"}""", [], [ContextTags.Inference]));

        var firstEvents = new List<AgentTurnEvent>();
        await foreach (var ev in agent.Run(AgentTurn.Text("turn 1", model: "qwen3.5:4b"), ct))
        {
            firstEvents.Add(ev);
        }
        var firstSelected = Assert.Single(firstEvents.OfType<AgentTurnEvent.ModelSelected>());
        Assert.Equal("qwen3.5:4b", firstSelected.Model.Model);

        // 2. Switch model at runtime to "claude-sonnet-4-5" (reconfigure idle agent + update context + turn)
        var stateAfterFirst = await agent.Read();
        await agent.Configure(stateAfterFirst.Definition with { Model = new ModelSelection("claude-sonnet-4-5") }, stateAfterFirst.Revision);
        await profileContext.Set(new ContextEntry("inference", """{"model":"claude-sonnet-4-5"}""", [], [ContextTags.Inference]));

        var secondEvents = new List<AgentTurnEvent>();
        await foreach (var ev in agent.Run(AgentTurn.Text("turn 2", model: "claude-sonnet-4-5"), ct))
        {
            secondEvents.Add(ev);
        }
        var secondSelected = Assert.Single(secondEvents.OfType<AgentTurnEvent.ModelSelected>());
        Assert.Equal("claude-sonnet-4-5", secondSelected.Model.Model);

        // 3. Switch model again to "gpt-5" and run via background Start
        var stateAfterSecond = await agent.Read();
        await agent.Configure(stateAfterSecond.Definition with { Model = new ModelSelection("gpt-5") }, stateAfterSecond.Revision);
        await profileContext.Set(new ContextEntry("inference", """{"model":"gpt-5"}""", [], [ContextTags.Inference]));
        var thirdRunId = await agent.Start(AgentTurn.Text("turn 3", model: "gpt-5"));
        await runtime.WaitForSignals<TurnCompleted>(id, 3, Within, ct);

        var finalState = await agent.Read();
        Assert.Equal(3, finalState.Runs.Length);
        Assert.Equal("qwen3.5:4b", finalState.Runs[0].Model?.Model);
        Assert.Equal("claude-sonnet-4-5", finalState.Runs[1].Model?.Model);
        Assert.Equal("gpt-5", Assert.Single(finalState.Runs, r => r.RunId == thirdRunId).Model?.Model);
    }

    [Fact]
    public async Task StartInvokedFromAppCallerRedactsOwnerSecretsWithoutTriggeringZeroTokenAppDenial()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, ct, siloServices: services =>
        {
            services.AddSingleton<IChatClient>(StubChatClient.Replying("Here is a joke and token sk_live_topsecret999!"));
            services.AddSingleton(new OsNeuronSurface(null!, null!, null!));
            services.AddSingleton<IAiCredentials, NoProviders>();
            services.AddSingleton<ISecretValueSource>(sp => new ZeroTokenGuardedSecretSource(sp.GetRequiredService<ICallerAmbient>(), "sk_live_topsecret999"));
            services.AddSingleton<ISecretRedactor, SecretRedactor>();
            services.AddOptions<AIOptions>();
            services.AddSingleton<ModelCatalog>();
        });
        using var person = runtime.As(Who.Person);

        var id = runtime.Id("agent", "ino/conversations/main");
        var agent = runtime.Get<IAgent>(id);
        var appCaller = runtime.Edge.App("owner/apps/ino", new Actor.Person(runtime.Brain.Value, runtime.Brain.Value), runtime.Brain);

        string runId;
        using (runtime.Edge.Enter(appCaller))
        {
            runId = await agent.Start(Turn("Hi, tell me a joke"));
        }

        await runtime.WaitForSignals<TurnCompleted>(id, 1, Within, ct);

        var completedRun = Assert.Single((await agent.Read()).Runs, r => r.RunId == runId);
        Assert.Equal(AgentRunStatus.Completed, completedRun.Status);
        Assert.Equal("Here is a joke and token ****!", completedRun.Reply);
    }

    private static AgentTurn Turn(string text) => new(new AiMessage("user", [new AiText(text)]));

    private async Task<INeuronRuntime> Start(StubChatClient client, CancellationToken ct)
        => await fixture.Start(NeuronRuntimeKind.Silo, ct, siloServices: services =>
        {
            services.AddSingleton<IChatClient>(client);
            // The loop resolves the surface for every turn but touches it only when a contract is composed; these turns compose none.
            services.AddSingleton(new OsNeuronSurface(null!, null!, null!));
            services.AddSingleton<IAiCredentials, NoProviders>();
            services.AddSingleton<ISecretRedactor, NoSecrets>();
            services.AddOptions<AIOptions>();
            services.AddSingleton<ModelCatalog>();
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

    // The run masks what it stores and announces; with no vault there is nothing to mask.
    private sealed class NoSecrets : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken) => ValueTask.FromResult(text);

        public ValueTask<int> TailLength(CancellationToken cancellationToken) => ValueTask.FromResult(0);
    }

    private sealed class ZeroTokenGuardedSecretSource(ICallerAmbient callers, params string[] secrets) : ISecretValueSource
    {
        public ValueTask<IReadOnlyList<string>> CurrentValues(CancellationToken cancellationToken)
        {
            if (CallerRules.IsApp(callers.Current, out _))
            {
                throw new UnauthorizedAccessException("Zero-token auth enforcement: sandbox and app principals cannot read raw vault secret values directly.");
            }
            return ValueTask.FromResult<IReadOnlyList<string>>(secrets);
        }
    }
}
