using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Brains;
using IntoChat.OS.Host.Client;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Host.Surface;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Voice;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Tests;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Voice;

public sealed class ConversationChatClientFacts(AiFixture fixture) : IClassFixture<AiFixture>
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task DeltasStreamUntilTheRunCloses()
    {
        var ct = TestContext.Current.CancellationToken;
        static async IAsyncEnumerable<ChatResponseUpdate> Stream(
            IReadOnlyList<ChatMessage> _,
            ChatOptions? __,
            [EnumeratorCancellation] CancellationToken token)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Hello ");
            await Task.Delay(120, token);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "there.");
        }

        await using var runtime = await Start(StubChatClient.StreamingOnly(Stream), ct);
        using var person = runtime.As(Who.Person);
        var convId = ConversationId.New();
        var id = runtime.Id(IConversation.Type, IConversation.KeyOf(convId));
        var conversation = runtime.Get<IConversation>(id);
        await conversation.Seat("ino", new AgentDefinition { Instructions = "x" });

        var watch = runtime.Services.GetRequiredService<IIntoChatOSBrains>().Open(runtime.Brain.Value);
        using var client = new ConversationChatClient(conversation, watch);

        var chunks = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                chunks.Add(update.Text);
            }
        }

        Assert.Equal("Hello there.", string.Concat(chunks));
        Assert.True(chunks.Count >= 2, $"Expected multiple streamed deltas, got {chunks.Count}.");
        var view = await conversation.Read();
        Assert.Equal(["hi", "Hello there."], view.Entries.OfType<Said>().Select(s => s.Text));
    }

    [Fact]
    public async Task CancellingStopsTheConversationRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var providerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<ChatResponseUpdate> SlowStream(
            IReadOnlyList<ChatMessage> _,
            ChatOptions? __,
            [EnumeratorCancellation] CancellationToken token)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Part one. ");
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                providerCancelled.TrySetResult();
                throw;
            }
        }

        await using var runtime = await Start(StubChatClient.StreamingOnly(SlowStream), ct);
        using var person = runtime.As(Who.Person);
        var convId = ConversationId.New();
        var id = runtime.Id(IConversation.Type, IConversation.KeyOf(convId));
        var conversation = runtime.Get<IConversation>(id);
        await conversation.Seat("ino", new AgentDefinition { Instructions = "x" });

        var watch = runtime.Services.GetRequiredService<IIntoChatOSBrains>().Open(runtime.Brain.Value);
        using var client = new ConversationChatClient(conversation, watch);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await using (var enumerator = client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "long")], cancellationToken: cts.Token).GetAsyncEnumerator(cts.Token))
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Contains("Part one.", enumerator.Current.Text, StringComparison.Ordinal);
            await cts.CancelAsync();
        }

        await providerCancelled.Task.WaitAsync(Within, ct);
        var view = await WaitUntilIdle(conversation, minRuns: 1, ct);
        Assert.Equal(AgentRunStatus.Cancelled, view.Runs[^1].Status);
    }

    private static async Task<ConversationView> WaitUntilIdle(IConversation conversation, int minRuns, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < Within)
        {
            var view = await conversation.Read();
            if (view.Active is null && view.Runs.Length >= minRuns)
            {
                return view;
            }
            await Task.Delay(30, ct);
        }
        return await conversation.Read();
    }

    private async Task<INeuronRuntime> Start(StubChatClient client, CancellationToken ct)
        => await fixture.Start(NeuronRuntimeKind.Silo, ct, siloServices: services =>
        {
            services.AddIntoChatOSClient();
            services.AddSingleton<IChatClient>(client);
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

    private sealed class NoSecrets : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken) => ValueTask.FromResult(text);
        public ValueTask<int> TailLength(CancellationToken cancellationToken) => ValueTask.FromResult(0);
    }
}
