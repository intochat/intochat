using DigitalBrain;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Testing;
using IntoChat.OS.Connections.Platform.Secrets;
using IntoChat.OS.Connections.Surface;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Observers;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

// The tone observer reads an agent's transcript after each turn. A turn needs the model loop, which reaches contexts
// through the silo's grain factory: silo runtime only.
public sealed class ToneObserverFacts(AiFixture fixture) : IClassFixture<AiFixture>
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ThreeShortTurnsSetConcise()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await Start(ct);
        var (agent, id) = await Watched(runtime, "short");

        await Talk(runtime, agent, id, 0, ["hi", "and then?", "ok go on"], ct);

        var tone = Assert.Single(await Entries(runtime, ct, entries => entries.Length > 0), entry => entry.Name == "tone");
        Assert.Equal("""{"persona":"concise"}""", tone.Text);
        Assert.Equal([ContextTags.Inference], tone.EffectiveTags);
    }

    [Fact]
    public async Task ALongTurnClearsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await Start(ct);
        var (agent, id) = await Watched(runtime, "long");
        await Talk(runtime, agent, id, 0, ["hi", "and then?", "ok go on"], ct);
        await Entries(runtime, ct, entries => entries.Length > 0);

        await Talk(runtime, agent, id, 3, ["Please walk me through the whole migration plan in detail."], ct);

        Assert.Empty(await Entries(runtime, ct, entries => entries.Length == 0));
    }

    private static async Task<(IAgent Agent, NeuronId Id)> Watched(INeuronRuntime runtime, string name)
    {
        var id = runtime.Id("agent", "agents/tone-" + name);
        var agent = runtime.Get<IAgent>(id);
        await agent.Configure(new(), 0);
        await runtime.Get<IToneObserver>(runtime.Id("ai.tone-observer", "observer/tone/agents/tone-" + name)).Watch("agents/tone-" + name);
        return (agent, id);
    }

    private static async Task Talk(INeuronRuntime runtime, IAgent agent, NeuronId id, int done, string[] texts, CancellationToken ct)
    {
        for (var turn = 0; turn < texts.Length; turn++)
        {
            await agent.Start(new AgentTurn(new AiMessage("user", [new AiText(texts[turn])])));
            await runtime.WaitForSignals<TurnCompleted>(id, done + turn + 1, Within, ct);
        }
    }

    private static async Task<ContextEntry[]> Entries(INeuronRuntime runtime, CancellationToken ct, Func<ContextEntry[], bool> done)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Within);
        var context = runtime.Get<IAgentContext>(runtime.Id("agent-context", "context/conversation.inference.tone"));
        while (true)
        {
            var entries = (await context.Read()).Entries;
            if (done(entries)) { return entries; }
            await Task.Delay(50, limit.Token);
        }
    }

    private async Task<INeuronRuntime> Start(CancellationToken ct)
        => await fixture.Start(NeuronRuntimeKind.Silo, ct, siloServices: services =>
        {
            services.AddSingleton<IChatClient>(StubChatClient.Replying("ok"));
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
