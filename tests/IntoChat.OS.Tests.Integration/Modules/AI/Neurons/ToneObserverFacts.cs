using System.Diagnostics;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Observers;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Tests;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

// The tone observer follows ConversationChanged and reads the conversation after each completed turn. A turn needs the
// model loop, which reaches contexts through the silo's grain factory: silo runtime only.
public sealed class ToneObserverFacts(AiFixture fixture) : IClassFixture<AiFixture>
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ThreeShortTurnsSetConcise()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await Start(ct);
        using var person = runtime.As(Who.Person);
        var conversation = await Watched(runtime, "short");

        await Talk(conversation, 0, ["hi", "and then?", "ok go on"], ct);

        var tone = Assert.Single(await Entries(runtime, ct, entries => entries.Length > 0), entry => entry.Name == "tone");
        Assert.Equal("""{"persona":"concise"}""", tone.Text);
        Assert.Equal([ContextTags.Inference], tone.EffectiveTags);
    }

    [Fact]
    public async Task ALongTurnClearsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await Start(ct);
        using var person = runtime.As(Who.Person);
        var conversation = await Watched(runtime, "long");
        await Talk(conversation, 0, ["hi", "and then?", "ok go on"], ct);
        await Entries(runtime, ct, entries => entries.Length > 0);

        await Talk(conversation, 3, ["Please walk me through the whole migration plan in detail."], ct);

        Assert.Empty(await Entries(runtime, ct, entries => entries.Length == 0));
    }

    private static async Task<IConversation> Watched(INeuronRuntime runtime, string name)
    {
        var key = IConversation.KeyOf(ConversationId.Parse("tone-" + name));
        var id = runtime.Id(IConversation.Type, key);
        var conversation = runtime.Get<IConversation>(id);
        await conversation.Seat("ino", new AgentDefinition());
        await runtime.Get<IToneObserver>(runtime.Id("ai.tone-observer", "observer/tone/" + key)).Follow(key);
        return conversation;
    }

    private static async Task Talk(IConversation conversation, int done, string[] texts, CancellationToken ct)
    {
        for (var turn = 0; turn < texts.Length; turn++)
        {
            await conversation.Say(texts[turn]);
            await WaitUntilIdle(conversation, done + turn + 1, ct);
        }
    }

    private static async Task WaitUntilIdle(IConversation conversation, int minRuns, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < Within)
        {
            var view = await conversation.Read();
            if (view.Active is null && view.Runs.Length >= minRuns) { return; }
            await Task.Delay(30, ct);
        }
        var final = await conversation.Read();
        Assert.True(final.Active is null && final.Runs.Length >= minRuns, $"Conversation did not finish {minRuns} runs within timeout.");
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
            services.AddSingleton(new NeuronSurface());
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
