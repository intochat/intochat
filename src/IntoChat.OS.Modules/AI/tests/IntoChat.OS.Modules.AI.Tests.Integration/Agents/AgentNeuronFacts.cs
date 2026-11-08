using DigitalBrain;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Testing;
using IntoChat.OS.Connections.Platform.Secrets;
using IntoChat.OS.Connections.Surface;
using IntoChat.OS.Modules.AI.Agents;
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

    private static AgentTurn Turn(string text) => new(new AiMessage("user", [new AiText(text)]));

    private async Task<INeuronRuntime> Start(StubChatClient client, CancellationToken ct)
        => await fixture.Start(NeuronRuntimeKind.Silo, ct, siloServices: services =>
        {
            services.AddSingleton<IChatClient>(client);
            // The loop resolves the surface for every turn but touches it only when a contract is composed; these turns compose none.
            services.AddSingleton(new NeuronSurface(null!, null!, null!));
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
}
