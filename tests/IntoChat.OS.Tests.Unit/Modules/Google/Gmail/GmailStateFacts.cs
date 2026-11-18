using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Invocation;
using DigitalBrain.Kernel;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Modules.Google.Gmail.Tests.Unit;

// The Gmail neuron is plain (06-08): a save that fails leaves its state as it was, and the next save starts from that state.
public sealed class GmailStateFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FailedAggregateWritePreservesThePreviousSnapshotOnTheSameActivation()
    {
        var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddKeyedSingleton(GmailModule.ClockKey, TimeProvider.System)
            .AddSingleton<IGmailApi>(new FakeDraftGmailApi())
            .AddSingleton<ISecretSealer>(new PassthroughSealer())
            .AddSingleton<IModuleIntegrations<GmailModule>>(new FakeIntegrations())
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider();

        await using var os = TestOS.Local(o =>
        {
            o.Services = services;
        }).Register(typeof(GmailModule).Assembly);

        var brain = new BrainId("facts");
        var id = new NeuronId(brain, "gmail", "connection/gmail/c1");
        os.SeedStoredState(id, JsonSerializer.Serialize(new GmailState
        {
            AuthMethodId = "google-oauth",
            Status = ConnectionStatus.Connected,
            GrantGeneration = 7,
            BoundMailbox = "fixture@example.test",
            Account = "fixture@example.test",
            PageTokenKey = Enumerable.Range(1, 32).Select(static i => (byte)i).ToArray(),
            Scopes = $"{GmailScopes.Read} https://www.googleapis.com/auth/gmail.compose",
            Secrets = new Dictionary<string, SealedSecret>(StringComparer.Ordinal)
            {
                ["access"] = new() { KeyId = "mk", Cipher = "token-1" },
            },
        }, RuntimeProtocol.OptionsFor(typeof(GmailState))));

        using var caller = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("user-1", "acc-1", brain));
        using var edge = os.Edge.Enter(os.Edge.Person("user-1", "acc-1", brain));

        var gmail = os.Get<IGmail>(id);
        var first = await gmail.PreviewDraft(new GmailDraftRequest(["a@example.test"], [], [], "First", "Body-1"), Ct);
        Assert.NotNull(first.Value);

        os.FailNextPublishAfterAppend(id);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gmail.PreviewDraft(new GmailDraftRequest(["b@example.test"], [], [], "Failed", "Body-2"), Ct));

        var stateJsonAfterFailure = await ((IBrainObserver)os).State(id, Ct);
        Assert.NotNull(stateJsonAfterFailure);
        var stateAfterFailure = stateJsonAfterFailure.Value.Deserialize<GmailState>(RuntimeProtocol.OptionsFor(typeof(GmailState)))!;
        var preservedPreview = Assert.Single(stateAfterFailure.DraftPreviews);
        Assert.Equal("First", preservedPreview.Value.Subject);
        Assert.Equal(7, stateAfterFailure.GrantGeneration);

        var created = await gmail.CreateDraft(new GmailCreateDraftRequest(first.Value!.PreviewId, "req-1"), Ct);
        Assert.NotNull(created.Value);
        Assert.Equal("draft-1", created.Value!.DraftId);

        var recovered = await gmail.PreviewDraft(new GmailDraftRequest(["c@example.test"], [], [], "Recovered", "Body-3"), Ct);
        Assert.NotNull(recovered.Value);

        var stateJsonAfterRecovery = await ((IBrainObserver)os).State(id, Ct);
        Assert.NotNull(stateJsonAfterRecovery);
        var stateAfterRecovery = stateJsonAfterRecovery.Value.Deserialize<GmailState>(RuntimeProtocol.OptionsFor(typeof(GmailState)))!;
        Assert.Equal(["First", "Recovered"], stateAfterRecovery.DraftPreviews.Select(p => p.Value.Subject).ToArray());
    }

    private sealed class FakeIntegrations : IModuleIntegrations<GmailModule>
    {
        public IIntegrationHandle Get(string integrationId) => new FakeHandle(integrationId);

        private sealed class FakeHandle(string integrationId) : IIntegrationHandle
        {
            public string IntegrationId => integrationId;
            public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new IntegrationSnapshot { IntegrationId = integrationId, Status = IntegrationStatus.Unconfigured });
            public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }

    private sealed class PassthroughSealer : ISecretSealer
    {
        public SealedSecret Seal(string name, string plaintext) => new() { KeyId = "mk", Cipher = plaintext };
        public string Open(SealedSecret secret) => secret.Cipher;
    }

    private sealed class FakeDraftGmailApi : IGmailApi
    {
        private int _draftCount;

        public Task<GoogleDraftWriteResult> CreateDraftAsync(string accessToken, GoogleCallContext context, byte[] mime, CancellationToken ct)
            => Task.FromResult(new GoogleDraftWriteResult(GoogleWriteOutcome.Succeeded, DraftId: $"draft-{++_draftCount}"));

        public Task<GoogleApiResult<GoogleProfile>> ProfileAsync(string accessToken, GoogleCallContext context, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<GoogleApiResult<GoogleMessagePage>> ListMessagesAsync(string accessToken, GoogleCallContext context, int take, string? pageToken, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<GoogleApiResult<GoogleMessageMetadata>> ReadMetadataAsync(string accessToken, GoogleCallContext context, string messageId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<GoogleApiResult<GoogleMessageContent>> ReadMessageAsync(string accessToken, GoogleCallContext context, string messageId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<GoogleApiResult<GoogleWatch>> WatchAsync(string accessToken, GoogleCallContext context, string topic, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<GoogleApiResult<GoogleUnit>> StopAsync(string accessToken, GoogleCallContext context, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<GoogleApiResult<GoogleHistoryPage>> HistoryAsync(string accessToken, GoogleCallContext context, string startHistoryId, string? pageToken, int take, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
