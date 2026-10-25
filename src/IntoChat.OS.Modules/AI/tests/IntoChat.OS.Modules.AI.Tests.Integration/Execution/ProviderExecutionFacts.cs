using IntoChat.OS.Modules.AI.Models;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

public sealed class ProviderExecutionFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task PinnedProfileCannotOverwriteHostingCeiling()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AIModule, AIOptions>(options =>
        {
            options.Hosting.MaxOutputTokens = 32;
            options.ModelProfiles["large"] = new() { Provider = "OpenAI", Model = "private-chat", MaxOutputTokens = 1000 };
        }).WithProvider("OpenAI").StartAsync(ct);
        var catalog = brain.Services.GetRequiredService<ModelCatalog>();
        var resolved = await catalog.Resolve(new("large"), false, ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.CreateClient(resolved, ct));
    }

    [Fact]
    public async Task StreamingHoldsSlotUntilEnumeratorIsDisposed()
    {
        using var gate = new ProviderExecutionGate(Options.Create(new AIOptions
        { Hosting = new AIHostingOptions { MaxConcurrentRequests = 1, MaxExecutionSeconds = 1, MaxOutputTokens = 32 } }));
        using var client = new LimitedChatClient(new WaitingClient(new()), gate);
        var iterator = client.GetStreamingResponseAsync([], cancellationToken: TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await iterator.MoveNextAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([], cancellationToken: TestContext.Current.CancellationToken));
        await iterator.DisposeAsync();
        using var admitted = gate.Enter(TestContext.Current.CancellationToken);
    }
    [Fact]
    public async Task DifferentModelClientsShareAdmissionAndCancellationReleasesSlot()
    {
        var options = Options.Create(new AIOptions
        { Hosting = new AIHostingOptions { MaxConcurrentRequests = 1, MaxExecutionSeconds = 1, MaxOutputTokens = 32 } });
        var registration = new ServiceCollection().AddSingleton<IOptions<AIOptions>>(options);
        AIClients.Add(registration);
        using var services = registration.BuildServiceProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var one = AIClients.BuildChatPipeline(services, false, "one", new WaitingClient(started), useFunctionInvocation: false);
        using var two = AIClients.BuildChatPipeline(services, false, "two", new WaitingClient(started), useFunctionInvocation: false);
        var ct = TestContext.Current.CancellationToken;
        var running = one.GetResponseAsync([new ChatMessage(ChatRole.User, "one")], cancellationToken: ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => two.GetResponseAsync([new ChatMessage(ChatRole.User, "two")], cancellationToken: ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => two.GetResponseAsync([new ChatMessage(ChatRole.User, "three")], cancellationToken: ct));
    }

    [Fact]
    public async Task CallerCannotRaiseConfiguredOutputCeiling()
    {
        using var gate = new ProviderExecutionGate(Options.Create(new AIOptions
        { Hosting = new AIHostingOptions { MaxConcurrentRequests = 1, MaxExecutionSeconds = 1, MaxOutputTokens = 32 } }));
        using var client = new LimitedChatClient(new WaitingClient(new()), gate);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([], new ChatOptions { MaxOutputTokens = 33 }, TestContext.Current.CancellationToken));
    }

    private sealed class WaitingClient(TaskCompletionSource started) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal(32, options?.MaxOutputTokens);
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Assert.Equal(32, options?.MaxOutputTokens);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial");
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

