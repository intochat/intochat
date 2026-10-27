using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Models.OpenAI;
using IntoChat.OS.Modules.AI.Models.Signals;
using IntoChat.OS.Connections.Platform.Integrations;
using Microsoft.Extensions.AI;
using Xunit;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

public sealed class AsyncCredentialFacts
{
    [Fact]
    public void ProviderCatalogDescribesItsNeuronAndSignalContracts()
    {
        foreach (var connector in AIModule.Connectors.Where(connector => connector.Id != "tavily"))
        {
            Assert.Contains(typeof(ILLM), connector.Neurons);
            Assert.Contains(typeof(InferenceCompleted), connector.Signals);
            // A connector's neurons are grain contracts or plain contracts (06-05: the model neurons), its signals either kind.
            Assert.All(connector.Neurons, neuron => Assert.True(typeof(INeuron).IsAssignableFrom(neuron) || IntoChat.OS.Runtime.PlainNeuronContracts.Is(neuron)));
            Assert.All(connector.Signals, signal => Assert.True(typeof(Signal).IsAssignableFrom(signal) || typeof(DigitalBrain.Signal).IsAssignableFrom(signal)));
        }
        Assert.Empty(AIModule.Connectors.Single(connector => connector.Id == "tavily").Neurons);
    }

    [Fact]
    public async Task ProviderConstructionYieldsUntilTheScopedHandleReleasesItsSecret()
    {
        var ct = TestContext.Current.CancellationToken;
        var secret = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = new Handle(secret.Task);
        var credentials = new RegistrationCredentials(new Bindings(handle), TimeProvider.System);
        var pending = new OpenAIProviderFactory().CreateChatClient("test-model", new AIOptions(), credentials, cancellationToken: ct);
        Assert.False(pending.IsCompleted);
        Assert.Equal("ApiKey", handle.RequestedField);
        secret.SetResult("test-only");
        using var client = await pending;
        Assert.NotNull(client);
    }

    [Fact]
    public async Task CancellingSecretReleaseReachesTheScopedHandle()
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var secret = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = new Handle(secret.Task);
        var credentials = new RegistrationCredentials(new Bindings(handle), TimeProvider.System);
        var pending = credentials.ReleaseSecretAsync("openai", "ApiKey", lifetime.Token);
        Assert.False(pending.IsCompleted);
        await lifetime.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(lifetime.Token, handle.SecretCancellation);
    }

    [Fact]
    public async Task StreamingAwaitsClientConstructionAndRebuildsOnlyWhenRevisionChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var built = 0;
        var revision = 1L;
        using var client = new RebuildingChatClient(async token =>
        {
            await ready.Task.WaitAsync(token);
            built++;
            return StubChatClient.StreamingOnly((_, _, _) => Updates());
        }, _ => Task.FromResult(revision));
        await using var stream = client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: ct).GetAsyncEnumerator(ct);
        var pending = stream.MoveNextAsync().AsTask();
        Assert.False(pending.IsCompleted);
        ready.SetResult();
        Assert.True(await pending);
        Assert.Equal("ok", stream.Current.Text);
        await Drain(client, ct);
        Assert.Equal(1, built);
        revision++;
        await Drain(client, ct);
        Assert.Equal(2, built);
    }

    [Fact]
    public async Task PausedMaterializationUsesOneRevisionForEndpointAndKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var released = new TaskCompletionSource<ReleasedIntegration>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = new RotatingHandle(released.Task);
        var credentials = new RegistrationCredentials(new Bindings(handle), TimeProvider.System);
        await credentials.StatusOf("openai", ct);
        var pending = new OpenAIProviderFactory().CreateChatClient("test-model", new AIOptions(), credentials, cancellationToken: ct);
        Assert.False(pending.IsCompleted);
        released.SetResult(new ReleasedIntegration
        {
            Snapshot = new IntegrationSnapshot { IntegrationId = "openai", Status = IntegrationStatus.Ready, Settings = new() { ["Endpoint"] = "http://127.0.0.1:32002" } },
            Values = new() { ["ApiKey"] = "new-key" },
        });
        using var client = await pending;
        Assert.Equal("http://127.0.0.1:32002/", ((ChatClientMetadata)client.GetService(typeof(ChatClientMetadata))!).ProviderUri!.AbsoluteUri);
        Assert.Equal(1, handle.Releases);
    }

    private sealed class RotatingHandle(Task<ReleasedIntegration> material) : IIntegrationHandle
    {
        public string IntegrationId => "openai";
        public int Releases { get; private set; }
        public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationSnapshot { IntegrationId = IntegrationId, Status = IntegrationStatus.Ready, Settings = new() { ["Endpoint"] = "http://127.0.0.1:32001" } });
        public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("A separate secret read could mix revisions.");
        public Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default)
        {
            Releases++;
            return material.WaitAsync(cancellationToken);
        }
    }

    private static async Task Drain(IChatClient client, CancellationToken ct)
    {
        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: ct)) { }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Updates()
    {
        await Task.CompletedTask;
        yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
    }

    private sealed class Bindings(IIntegrationHandle handle) : IModuleIntegrations<AIModule>
    {
        public IIntegrationHandle Get(string integrationId)
        {
            Assert.Equal("openai", integrationId);
            return handle;
        }
    }

    private sealed class Handle(Task<string> secret) : IIntegrationHandle
    {
        public string IntegrationId => "openai";
        public string? RequestedField { get; private set; }
        public CancellationToken SecretCancellation { get; private set; }
        public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationSnapshot { IntegrationId = IntegrationId, Status = IntegrationStatus.Ready });
        public async Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default)
            => new() { Snapshot = await ReadAsync(cancellationToken), Values = new() { ["ApiKey"] = await ReadSecretAsync("ApiKey", cancellationToken) } };
        public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default)
        {
            RequestedField = field;
            SecretCancellation = cancellationToken;
            return secret.WaitAsync(cancellationToken);
        }
    }
}
