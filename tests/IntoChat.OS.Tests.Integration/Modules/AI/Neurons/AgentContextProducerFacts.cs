using IntoChat.OS.Tests;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Testing;
using IntoChat.OS.Connections.Surface;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using DigitalBrain.Capabilities.Secrets;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

// The agent writes its conversation summary and memory recall into contexts it lists, so both reach the model as ordinary
// entries. A turn needs the model loop, which reaches contexts through the silo's grain factory: silo runtime only.
public sealed class AgentContextProducerFacts(AiFixture fixture) : IClassFixture<AiFixture>
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task TheSummaryIsAContextEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = StubChatClient.Replying("ok");
        await using var runtime = await Start(client, ct);
        using var person = runtime.As(Who.Person);
        var id = runtime.Id("agent", "agents/summary");
        var agent = runtime.Get<IAgent>(id);
        await agent.Configure(new() { Contexts = [ContextKeys.Of(runtime.Brain.Value, "conversation.summary")], MaxHistoryMessages = 2 }, 0);

        // The second turn overflows the two kept messages, so the first is folded into the summary after it.
        string[] inputs = ["first", "second", "third"];
        for (var turn = 0; turn < inputs.Length; turn++)
        {
            await agent.Start(Turn(inputs[turn]));
            await runtime.WaitForSignals<TurnCompleted>(id, turn + 1, Within, ct);
        }

        var snapshot = await runtime.Get<IAgentContext>(runtime.Id("agent-context", "context/conversation.summary")).Read();
        Assert.StartsWith("Earlier conversation summary: ", Assert.Single(snapshot.Entries, entry => entry.Name == "summary").Text);
        var summaries = client.Requests[^1].Where(message => message.Role == ChatRole.System
            && message.Text.StartsWith("Earlier conversation summary: ", StringComparison.Ordinal)).ToArray();
        // Exactly one: the entry, not also a line the loop adds from the plan.
        Assert.Contains("first", Assert.Single(summaries).Text);
    }

    [Fact]
    public async Task RecallIsWrittenBeforeTheTurnReads()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = StubChatClient.Replying("ok");
        await using var runtime = await Start(client, ct);
        using var person = runtime.As(Who.Person);
        await runtime.Get<IMemory>(runtime.Id("ai.memory", "memory")).RememberFact("preferences", "User prefers dark mode", 9);
        var id = runtime.Id("agent", "agents/recall");
        var agent = runtime.Get<IAgent>(id);
        await agent.Configure(new() { Contexts = [ContextKeys.Of(runtime.Brain.Value, "memory.recall")] }, 0);

        await agent.Start(Turn("dark mode"));
        await runtime.WaitForSignals<TurnCompleted>(id, 1, Within, ct);

        var system = Assert.Single(client.Requests).Where(message => message.Role == ChatRole.System).Select(message => message.Text).ToArray();
        Assert.Contains(system, text => text.StartsWith("- ", StringComparison.Ordinal) && text.Contains("User prefers dark mode", StringComparison.Ordinal));
        var snapshot = await runtime.Get<IAgentContext>(runtime.Id("agent-context", "context/memory.recall")).Read();
        Assert.Single(snapshot.Entries, entry => entry.Name == "recall");
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
}
