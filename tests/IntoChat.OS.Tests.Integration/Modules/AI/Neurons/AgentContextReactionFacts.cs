using System.Runtime.CompilerServices;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using IntoChat.OS.Connections.Platform.Secrets;
using IntoChat.OS.Connections.Surface;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

// The agent follows the contexts it lists and blocks on a trusted interrupt. A turn needs the model loop, which reaches
// contexts through the silo's grain factory, so these facts run on the silo runtime only.
public sealed class AgentContextReactionFacts(AiFixture fixture) : IClassFixture<AiFixture>
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ATrustedInterruptCancelsTheActiveRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await Start(ct);
        var (agent, id) = await Agent(runtime, "cancel");

        var runId = await agent.Start(Turn("hello"));
        await runtime.WaitForSignals<TurnProgressed>(id, 1, Within, ct);
        await Interrupt(runtime, "cancel", ct);

        var failed = Assert.Single(await runtime.WaitForSignals<TurnFailed>(id, 1, Within, ct));
        Assert.Equal((runId, true), (failed.RunId, failed.Cancelled));
        var state = await Until(agent, state => state.Blocked && state.ActiveRun is null, ct);
        Assert.Equal(AgentRunStatus.Cancelled, Assert.Single(state.Runs).Status);
        Assert.True(Assert.Single(await runtime.WaitForSignals<AgentBlocked>(id, 1, Within, ct)).Blocked);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Start(Turn("again")));
        Assert.Equal("Blocked: halt now", refused.Message);
    }

    [Fact]
    public async Task ARelativeContextKeyBlocksTheAgent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await Start(ct);
        var agent = runtime.Get<IAgent>(runtime.Id("agent", "agents/react-relative"));
        await agent.Configure(new() { Contexts = ["context/relative-x"] }, 0);

        await Interrupt(runtime, "relative-x", ct);
        Assert.True((await Until(agent, state => state.Blocked, ct)).Blocked);

        using (RuntimeCallerContext.Stamp(CapabilityCallers.Platform("p1", "a1", runtime.Brain.Value))) { await runtime.Get<IAgentContext>(ContextId(runtime, "relative-x")).Remove("stop"); }
        Assert.False((await Until(agent, state => !state.Blocked, ct)).Blocked);
    }

    [Fact]
    public async Task RemovingTheInterruptUnblocks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await Start(ct);
        var (agent, id) = await Agent(runtime, "unblock");
        await Interrupt(runtime, "unblock", ct);
        await Until(agent, state => state.Blocked, ct);

        using (RuntimeCallerContext.Stamp(CapabilityCallers.Platform("p1", "a1", runtime.Brain.Value))) { await runtime.Get<IAgentContext>(ContextId(runtime, "unblock")).Remove("stop"); }

        await Until(agent, state => !state.Blocked, ct);
        var signals = await runtime.WaitForSignals<AgentBlocked>(id, 2, Within, ct);
        Assert.Equal([true, false], signals.Select(signal => signal.Blocked));
        var runId = await agent.Start(Turn("hello"));
        Assert.Equal(runId, (await agent.Read()).ActiveRun?.RunId);
        await agent.Cancel();
    }

    [Fact]
    public async Task BlockedIsRecomputedOnActivation()
    {
        // The fixture has no silo restart, so this asserts the stored flag follows the contexts without a turn.
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await Start(ct);
        await Interrupt(runtime, "recompute", ct);
        var (agent, _) = await Agent(runtime, "recompute");
        Assert.True((await agent.Read()).Blocked);

        using (RuntimeCallerContext.Stamp(CapabilityCallers.Platform("p1", "a1", runtime.Brain.Value))) { await runtime.Get<IAgentContext>(ContextId(runtime, "recompute")).Remove("stop"); }

        Assert.False((await Until(agent, state => !state.Blocked, ct)).Blocked);
    }

    private static async Task<AgentState> Until(IAgent agent, Func<AgentState, bool> done, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Within);
        while (true)
        {
            var state = await agent.Read();
            if (done(state)) { return state; }
            await Task.Delay(50, limit.Token);
        }
    }

    private static async Task<(IAgent Agent, NeuronId Id)> Agent(INeuronRuntime runtime, string name)
    {
        var id = runtime.Id("agent", "agents/react-" + name);
        var agent = runtime.Get<IAgent>(id);
        await agent.Configure(new() { Contexts = [ContextKeys.Of(runtime.Brain.Value, name)] }, 0);
        return (agent, id);
    }

    private static Task Interrupt(INeuronRuntime runtime, string name, CancellationToken ct) => Task.Run(async () =>
    {
        using var _ = RuntimeCallerContext.Stamp(CapabilityCallers.Platform("p1", "a1", runtime.Brain.Value));
        await runtime.Get<IAgentContext>(ContextId(runtime, name)).Set(new("stop", "halt now", [], [ContextTags.Interrupt]));
    }, ct);

    private static NeuronId ContextId(INeuronRuntime runtime, string name) => runtime.Id("agent-context", "context/" + name);

    private static AgentTurn Turn(string text) => new(new AiMessage("user", [new AiText(text)]));

    // Streams a first word, then waits until the run is cancelled.
    private static async IAsyncEnumerable<ChatResponseUpdate> Endless(IReadOnlyList<ChatMessage> messages, ChatOptions? options, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, "thinking");
        await Task.Delay(Timeout.Infinite, ct);
    }

    private async Task<INeuronRuntime> Start(CancellationToken ct)
        => await fixture.Start(NeuronRuntimeKind.Silo, ct, siloServices: services =>
        {
            services.AddSingleton<IChatClient>(StubChatClient.StreamingOnly(Endless));
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
}
