using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Limits;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Modules.Google.Gmail.Tests.Unit;

public sealed class GmailPolicyFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task Adopt(TestOS os, PolicyRule rule) => os.AdoptPolicy("b", rule, Ct);

    private static Task<(TestOS Os, IGmail Neuron, NeuronId Id, FakeGmailApi Api)> CreateHarnessAsync()
    {
        var api = new FakeGmailApi();
        var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddKeyedSingleton(GmailModule.ClockKey, TimeProvider.System)
            .AddSingleton<IGmailApi>(api)
            .AddSingleton<ISecretSealer>(new PassthroughSealer())
            .AddSingleton<DigitalBrain.Capabilities.Connections.Integrations.IModuleIntegrations<GmailModule>>(new FakeIntegrations())
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider();
        var os = TestOS.Local(o => o.Services = services).Register(typeof(GmailModule).Assembly);

        var id = new NeuronId(new BrainId("b"), "gmail", "connection/gmail/c1");
        os.SeedStoredState(
            id,
            JsonSerializer.Serialize(
                new GmailState
                {
                    Status = ConnectionStatus.Connected,
                    Account = "owner@example.test",
                    Scopes = $"{GmailScopes.Read} https://www.googleapis.com/auth/gmail.compose",
                    Secrets = new Dictionary<string, SealedSecret>(StringComparer.Ordinal)
                    {
                        ["access"] = new SealedSecret { KeyId = "mk", Cipher = "token-1" },
                    },
                },
                DigitalBrain.Invocation.RuntimeProtocol.OptionsFor(typeof(GmailState))));

        return Task.FromResult((os, os.Get<IGmail>(id), id, api));
    }

    [Fact]
    public async Task ARefusingPolicyStopsTheDraftBeforeTheApiCall()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("b", "b", new BrainId("b")));
        var (os, neuron, id, api) = await CreateHarnessAsync();
        await using var _ = os;
        var rule = new PolicyRule(
            "r1",
            "gmail.draft.create",
            PolicyMeasure.Count,
            new PolicyWindow(PolicyWindowKind.None, null),
            0m,
            null,
            "Never create a mail draft",
            os.Clock.GetUtcNow());
        await Adopt(os, rule);

        using var caller = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("user-1", "acc-1", new BrainId("b")));
        var preview = await neuron.PreviewDraft(new GmailDraftRequest(["alice@example.test"], [], [], "Hello", "Body"), Ct);
        Assert.NotNull(preview.Value);

        await Assert.ThrowsAsync<RuleViolationException>(
            () => neuron.CreateDraft(new GmailCreateDraftRequest(preview.Value.PreviewId, "req-1"), Ct));

        Assert.Equal(0, api.CreateDraftCalls);
        var stateJson = await ((DigitalBrain.Kernel.IBrainObserver)os).State(id, Ct);
        Assert.NotNull(stateJson);
        var state = stateJson.Value.Deserialize<GmailState>(DigitalBrain.Invocation.RuntimeProtocol.OptionsFor(typeof(GmailState)));
        Assert.NotNull(state);
        Assert.Empty(state.DraftOperations);
    }

    [Fact]
    public async Task AnAllowedDraftIsCountedOnce()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("b", "b", new BrainId("b")));
        var (os, neuron, _, api) = await CreateHarnessAsync();
        await using var _ = os;
        var rule = new PolicyRule(
            "r1",
            "gmail.draft.create",
            PolicyMeasure.Count,
            new PolicyWindow(PolicyWindowKind.Day, null),
            1m,
            null,
            "Allow at most 1 count of create a mail draft per day",
            os.Clock.GetUtcNow());
        await Adopt(os, rule);

        using var caller = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("user-1", "acc-1", new BrainId("b")));
        var firstPreview = await neuron.PreviewDraft(new GmailDraftRequest(["alice@example.test"], [], [], "First", "Body 1"), Ct);
        var first = await neuron.CreateDraft(new GmailCreateDraftRequest(firstPreview.Value!.PreviewId, "req-1"), Ct);
        Assert.Equal(GmailDraftOperationStatus.Completed, first.Value!.Status);
        Assert.Equal(1, api.CreateDraftCalls);

        var secondPreview = await neuron.PreviewDraft(new GmailDraftRequest(["bob@example.test"], [], [], "Second", "Body 2"), Ct);
        await Assert.ThrowsAsync<RuleViolationException>(
            () => neuron.CreateDraft(new GmailCreateDraftRequest(secondPreview.Value!.PreviewId, "req-2"), Ct));
        Assert.Equal(1, api.CreateDraftCalls);
    }

    [Fact]
    public async Task AFailedApiCallReleasesTheReservation()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("b", "b", new BrainId("b")));
        var (os, neuron, _, api) = await CreateHarnessAsync();
        await using var _ = os;
        var rule = new PolicyRule(
            "r1",
            "gmail.draft.create",
            PolicyMeasure.Count,
            new PolicyWindow(PolicyWindowKind.Day, null),
            1m,
            null,
            "Allow at most 1 count of create a mail draft per day",
            os.Clock.GetUtcNow());
        await Adopt(os, rule);

        using var caller = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("user-1", "acc-1", new BrainId("b")));
        api.NotDispatchedOnNextCreateDraft = true;
        var firstPreview = await neuron.PreviewDraft(new GmailDraftRequest(["alice@example.test"], [], [], "First", "Body 1"), Ct);
        var first = await neuron.CreateDraft(new GmailCreateDraftRequest(firstPreview.Value!.PreviewId, "req-1"), Ct);
        Assert.Equal(GmailDraftOperationStatus.Failed, first.Value!.Status);

        var secondPreview = await neuron.PreviewDraft(new GmailDraftRequest(["bob@example.test"], [], [], "Second", "Body 2"), Ct);
        var second = await neuron.CreateDraft(new GmailCreateDraftRequest(secondPreview.Value!.PreviewId, "req-2"), Ct);
        Assert.Equal(GmailDraftOperationStatus.Completed, second.Value!.Status);

        var thirdPreview = await neuron.PreviewDraft(new GmailDraftRequest(["carol@example.test"], [], [], "Third", "Body 3"), Ct);
        await Assert.ThrowsAsync<RuleViolationException>(
            () => neuron.CreateDraft(new GmailCreateDraftRequest(thirdPreview.Value!.PreviewId, "req-3"), Ct));
    }

    [Fact]
    public async Task AnUnknownOutcomeKeepsTheReservation()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("b", "b", new BrainId("b")));
        var (os, neuron, _, api) = await CreateHarnessAsync();
        await using var _ = os;
        var rule = new PolicyRule(
            "r1",
            "gmail.draft.create",
            PolicyMeasure.Count,
            new PolicyWindow(PolicyWindowKind.Day, null),
            1m,
            null,
            "Allow at most 1 count of create a mail draft per day",
            os.Clock.GetUtcNow());
        await Adopt(os, rule);

        using var caller = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("user-1", "acc-1", new BrainId("b")));
        api.ThrowOnNextCreateDraft = true;
        var firstPreview = await neuron.PreviewDraft(new GmailDraftRequest(["alice@example.test"], [], [], "First", "Body 1"), Ct);
        await Assert.ThrowsAsync<HttpRequestException>(
            () => neuron.CreateDraft(new GmailCreateDraftRequest(firstPreview.Value!.PreviewId, "req-1"), Ct));

        // The draft may exist on the provider, so the day's one draft stays spent.
        var secondPreview = await neuron.PreviewDraft(new GmailDraftRequest(["bob@example.test"], [], [], "Second", "Body 2"), Ct);
        await Assert.ThrowsAsync<RuleViolationException>(
            () => neuron.CreateDraft(new GmailCreateDraftRequest(secondPreview.Value!.PreviewId, "req-2"), Ct));
        Assert.Equal(1, api.CreateDraftCalls);
    }

    [Fact]
    public async Task ARecipientRuleSeesEveryAddressIncludingBcc()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("b", "b", new BrainId("b")));
        var (os, neuron, _, api) = await CreateHarnessAsync();
        await using var _ = os;
        var rule = new PolicyRule(
            "r1",
            "gmail.draft.create",
            PolicyMeasure.Count,
            new PolicyWindow(PolicyWindowKind.Day, null),
            1m,
            new PolicyCondition("To", PolicyOp.EndsWith, "@corp.com"),
            "Allow at most 1 count of create a mail draft per day when To EndsWith @corp.com",
            os.Clock.GetUtcNow());
        await Adopt(os, rule);

        using var caller = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("user-1", "acc-1", new BrainId("b")));
        // The corp address is not the last one in To; it still counts.
        var firstPreview = await neuron.PreviewDraft(new GmailDraftRequest(["boss@corp.com", "friend@other.org"], [], [], "First", "Body 1"), Ct);
        var first = await neuron.CreateDraft(new GmailCreateDraftRequest(firstPreview.Value!.PreviewId, "req-1"), Ct);
        Assert.Equal(GmailDraftOperationStatus.Completed, first.Value!.Status);

        // A corp address hidden in Bcc is refused by the same rule, and nothing is sent to the provider.
        var secondPreview = await neuron.PreviewDraft(new GmailDraftRequest(["friend@other.org"], [], ["team@corp.com"], "Second", "Body 2"), Ct);
        await Assert.ThrowsAsync<RuleViolationException>(
            () => neuron.CreateDraft(new GmailCreateDraftRequest(secondPreview.Value!.PreviewId, "req-2"), Ct));
        Assert.Equal(1, api.CreateDraftCalls);

        // Addresses outside the rule are unaffected.
        var thirdPreview = await neuron.PreviewDraft(new GmailDraftRequest(["friend@other.org"], ["pal@other.org"], [], "Third", "Body 3"), Ct);
        var third = await neuron.CreateDraft(new GmailCreateDraftRequest(thirdPreview.Value!.PreviewId, "req-3"), Ct);
        Assert.Equal(GmailDraftOperationStatus.Completed, third.Value!.Status);
    }

    [Fact]
    public void TheManifestDeclaresTheAction()
    {
        var action = Assert.Single(ModuleManifest.Of(typeof(GmailModule)).Actions, a => a.Name == "gmail.draft.create");
        Assert.Equal("create a mail draft", action.Description);
        Assert.Null(action.AmountUnit);
    }

    [Fact]
    public async Task PreviewDraftSucceedsAndUsesContextTimeWhenServiceProviderIsNotKeyed()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 5, 0, 0, TimeSpan.Zero));
        var api = new FakeGmailApi();
        var inner = new ServiceCollection()
            .AddSingleton<TimeProvider>(clock)
            .AddKeyedSingleton<TimeProvider>(GmailModule.ClockKey, clock)
            .AddSingleton<IGmailApi>(api)
            .AddSingleton<ISecretSealer>(new PassthroughSealer())
            .AddSingleton<DigitalBrain.Capabilities.Connections.Integrations.IModuleIntegrations<GmailModule>>(new FakeIntegrations())
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider();
        var nonKeyed = new NonKeyedServiceProvider(inner);

        await using var os = TestOS.Local(o => o.Services = nonKeyed).Register(typeof(GmailModule).Assembly);
        var id = new NeuronId(new BrainId("b"), "gmail", "connection/gmail/c1");
        os.SeedStoredState(
            id,
            JsonSerializer.Serialize(
                new GmailState
                {
                    Status = ConnectionStatus.Connected,
                    Account = "owner@example.test",
                    Scopes = $"{GmailScopes.Read} https://www.googleapis.com/auth/gmail.compose",
                    Secrets = new Dictionary<string, SealedSecret>(StringComparer.Ordinal)
                    {
                        ["access"] = new SealedSecret { KeyId = "mk", Cipher = "token-1" },
                    },
                },
                DigitalBrain.Invocation.RuntimeProtocol.OptionsFor(typeof(GmailState))));

        var neuron = os.Get<IGmail>(id);
        using var caller = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("user-1", "acc-1", new BrainId("b")));
        var preview = await neuron.PreviewDraft(new GmailDraftRequest(["alice@example.test"], [], [], "Hello", "Body"), Ct);
        Assert.NotNull(preview.Value);
        Assert.Equal(clock.GetUtcNow().UtcDateTime.Date, preview.Value.ExpiresAt.UtcDateTime.Date);
    }

    private sealed class NonKeyedServiceProvider(IServiceProvider inner) : IServiceProvider
    {
        public object? GetService(Type serviceType) => inner.GetService(serviceType);
    }

    private sealed class FakeGmailApi : IGmailApi
    {
        public int CreateDraftCalls { get; private set; }
        public bool ThrowOnNextCreateDraft { get; set; }
        public bool NotDispatchedOnNextCreateDraft { get; set; }

        public Task<GoogleDraftWriteResult> CreateDraftAsync(string accessToken, GoogleCallContext context, byte[] mime, CancellationToken ct)
        {
            CreateDraftCalls++;
            if (ThrowOnNextCreateDraft)
            {
                ThrowOnNextCreateDraft = false;
                throw new HttpRequestException("Simulated Gmail transport failure.");
            }
            if (NotDispatchedOnNextCreateDraft)
            {
                NotDispatchedOnNextCreateDraft = false;
                return Task.FromResult(new GoogleDraftWriteResult(GoogleWriteOutcome.NotDispatched, Failure: new GoogleApiFailure(GoogleApiFailureKind.Unavailable, 503)));
            }
            return Task.FromResult(new GoogleDraftWriteResult(GoogleWriteOutcome.Succeeded, DraftId: $"draft-{CreateDraftCalls}"));
        }

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

    private sealed class PassthroughSealer : ISecretSealer
    {
        public SealedSecret Seal(string name, string plaintext) => new() { KeyId = "mk", Cipher = plaintext };
        public string Open(SealedSecret secret) => secret.Cipher;
    }

    private sealed class FakeIntegrations : DigitalBrain.Capabilities.Connections.Integrations.IModuleIntegrations<GmailModule>
    {
        public DigitalBrain.Capabilities.Connections.Integrations.IIntegrationHandle Get(string integrationId) => new FakeHandle(integrationId);

        private sealed class FakeHandle(string integrationId) : DigitalBrain.Capabilities.Connections.Integrations.IIntegrationHandle
        {
            public string IntegrationId => integrationId;
            public Task<DigitalBrain.Capabilities.Connections.Integrations.IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new DigitalBrain.Capabilities.Connections.Integrations.IntegrationSnapshot { IntegrationId = integrationId, Status = DigitalBrain.Capabilities.Connections.Integrations.IntegrationStatus.Unconfigured });
            public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<DigitalBrain.Capabilities.Connections.Integrations.ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }
}
