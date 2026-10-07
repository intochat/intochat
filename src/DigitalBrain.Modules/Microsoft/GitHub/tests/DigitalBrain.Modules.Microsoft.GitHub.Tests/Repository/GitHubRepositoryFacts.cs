using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Connections.Signals;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Microsoft.GitHub;
using DigitalBrain.Microsoft.GitHub.Signals;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.GitHub.Tests.Repository;

public sealed class GitHubRepositoryFacts
{
    private static readonly string Scope = BrainScope.Create("owner", "owner").Id;
    private static readonly string Key = ConnectionKeys.For(Scope, "github", "digitalbrain");
    private static readonly string OtherKey = ConnectionKeys.For(BrainScope.Create("other", "other").Id, "github", "digitalbrain");
    private static readonly CallerContext Owner = new() { PrincipalId = "owner", AccountId = "owner", BrainId = "owner", Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp };
    private static readonly CallerContext Other = new() { PrincipalId = "other", AccountId = "other", BrainId = "other", Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp };

    private static Task<ModuleBrain> StartAsync(CancellationToken ct, IGitHubRepositorySource? source = null, DurableGrainStorage? storage = null, FakeInstallationVerifier? verifier = null) => ModuleTest.Create()
        .WithExecution(new() { PrivateConfiguration = new Dictionary<string, string?> { [IntegrationSchema.Key("github", "AppId")] = "7", [IntegrationSchema.Key("github", "PrivateKeyPem")] = "unused", [IntegrationSchema.Key("github", "WebhookSecret")] = "0123456789abcdef" } })
        .WithModule<GitHubModule>()
        .ConfigureSilo(silo =>
        {
            silo.Services.AddSingleton<IGitHubRepositorySource>(source ?? new FakeGitHubRepositorySource());
            silo.Services.AddSingleton<IGitHubUserAccess>(new FakeUserAccess());
            silo.Services.AddSingleton<IGitHubInstallationVerifier>(verifier ?? new FakeInstallationVerifier());
            storage?.Register(silo);
        })
        .StartAsync(ct);

    // A stamp set inside this awaited helper does not flow back to the caller.
    private static async Task<RepositoryView> ConnectAsync(ModuleBrain brain, CallerContext? caller = null, string? key = null)
    {
        CallerContextStamper.Stamp(caller ?? Owner);
        await brain.AuthorizeCallerAsync();
        await brain.Grains.GetConnectionInbound(key ?? Key).AcceptGrant(new("oauth", "user-token", null, null, "", "https://github.com/intochat/digitalbrain", "{}"));
        return await brain.Get<IRepository>(key ?? Key).Read();
    }

    [Fact]
    public async Task AcceptGrantResolvesTheInstallationAndConnects()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        try
        {
            await using var connected = await brain.Observe<ConnectionConnected>(brain.Get<IRepository>(Key), ct);
            var view = await ConnectAsync(brain);
            Assert.True(view.Connected);
            Assert.Equal("intochat/digitalbrain", (await connected.NextAsync(ct: ct)).Account);
            Assert.Equal([Key], await brain.Grains.GetGrain<IGitHubRepositoryIndex>("installation/9").Read());
        }
        finally { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
    }

    [Fact]
    public async Task TheUserTokenIsNeverKeptAfterTheGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        try
        {
            await ConnectAsync(brain);
            var view = await brain.Get<IConnection>(Key).Read();
            Assert.Equal(ConnectionStatus.Connected, view.Status);
            Assert.Null(view.ExpiresAt);
            Assert.DoesNotContain("user-token", System.Text.Json.JsonSerializer.Serialize(view), StringComparison.Ordinal);
        }
        finally { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
    }

    [Fact]
    public async Task AWebhookRoutesThroughTheRepositoryIndexToTheConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        try
        {
            await ConnectAsync(brain);
            await using var changed = await brain.Observe<PullRequestChanged>(brain.Get<IRepository>(Key), ct);
            var ingress = brain.SiloServices.GetRequiredService<GitHubWebhookIngress>();
            var body = System.Text.Encoding.UTF8.GetBytes("""{"action":"opened","number":1,"installation":{"id":9},"repository":{"id":11,"name":"digitalbrain","owner":{"login":"intochat"}}}""");
            Assert.Equal(GitHubWebhookAcceptance.Accepted, await ingress.HandleAsync(body, Headers(body, "pull_request", "0123456789abcdef", sameDelivery: true), ct));
            Assert.Equal(1, (await changed.NextAsync(ct: ct)).Number);
            Assert.Equal(GitHubWebhookAcceptance.Duplicate, await ingress.HandleAsync(body, Headers(body, "pull_request", "0123456789abcdef", sameDelivery: true), ct));
        }
        finally { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
    }

    [Fact]
    public async Task AWebhookWithAForgedSignatureReachesNoConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        try
        {
            await ConnectAsync(brain);
            var body = System.Text.Encoding.UTF8.GetBytes("""{"action":"opened","number":1,"installation":{"id":9},"repository":{"id":11,"name":"digitalbrain","owner":{"login":"intochat"}}}""");
            Assert.Equal(GitHubWebhookAcceptance.Unauthorized, await brain.SiloServices.GetRequiredService<GitHubWebhookIngress>().HandleAsync(body, Headers(body, "pull_request", "not-the-webhook-secret"), ct));
            Assert.Null((await brain.Get<IRepository>(Key).Read()).LastWebhookAt);
        }
        finally { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
    }

    [Fact]
    public async Task AReactivatedRepositoryStillReceivesWebhooks()
    {
        var ct = TestContext.Current.CancellationToken;
        // The restart drops every activation and silo singleton; only stored state routes the delivery.
        await using var brain = await StartAsync(ct, storage: new DurableGrainStorage());
        try
        {
            await ConnectAsync(brain);
            await brain.RestartSiloAsync(ct);
            await using var changed = await brain.Observe<PullRequestChanged>(brain.Get<IRepository>(Key), ct);
            var body = System.Text.Encoding.UTF8.GetBytes("""{"action":"opened","number":2,"installation":{"id":9},"repository":{"id":11,"name":"digitalbrain","owner":{"login":"intochat"}}}""");
            Assert.Equal(GitHubWebhookAcceptance.Accepted, await brain.SiloServices.GetRequiredService<GitHubWebhookIngress>().HandleAsync(body, Headers(body, "pull_request", "0123456789abcdef"), ct));
            Assert.Equal(2, (await changed.NextAsync(ct: ct)).Number);
        }
        finally { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
    }

    [Fact]
    public async Task ARevokedInstallationMarksTheConnectionFailing()
    {
        var ct = TestContext.Current.CancellationToken;
        var verifier = new FakeInstallationVerifier();
        await using var brain = await StartAsync(ct, verifier: verifier);
        try
        {
            await ConnectAsync(brain);
            await using var failing = await brain.Observe<ConnectionFailing>(brain.Get<IRepository>(Key), ct);
            var body = System.Text.Encoding.UTF8.GetBytes("""{"action":"deleted","installation":{"id":9}}""");
            await brain.SiloServices.GetRequiredService<GitHubWebhookIngress>().HandleAsync(body, Headers(body, "installation", "0123456789abcdef"), ct);
            await failing.NextAsync(ct: ct);
            Assert.False((await brain.Get<IRepository>(Key).Read()).Connected);
            await Assert.ThrowsAsync<GitHubAccessDeniedException>(() => brain.Get<IRepository>(Key).ReadPullRequests());
            Assert.Equal([Key], verifier.Forgotten);
            // The index keeps the key; the status alone keeps deliveries away from a revoked connection.
            Assert.Equal([Key], await brain.Grains.GetGrain<IGitHubRepositoryIndex>("installation/9").Read());
            var delivery = System.Text.Encoding.UTF8.GetBytes("""{"action":"opened","number":1,"installation":{"id":9},"repository":{"id":11,"name":"digitalbrain","owner":{"login":"intochat"}}}""");
            Assert.Equal(GitHubWebhookAcceptance.Ignored, await brain.SiloServices.GetRequiredService<GitHubWebhookIngress>().HandleAsync(delivery, Headers(delivery, "pull_request", "0123456789abcdef"), ct));
        }
        finally { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
    }

    [Fact]
    public async Task ARevokedConnectionThatProbesHealthyReceivesWebhooksAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        try
        {
            await ConnectAsync(brain);
            var ingress = brain.SiloServices.GetRequiredService<GitHubWebhookIngress>();
            var suspended = System.Text.Encoding.UTF8.GetBytes("""{"action":"suspend","installation":{"id":9}}""");
            await ingress.HandleAsync(suspended, Headers(suspended, "installation", "0123456789abcdef"), ct);
            Assert.Equal(ConnectionStatus.Failing, (await brain.Get<IConnection>(Key).Read()).Status);
            CallerContextStamper.Stamp(Owner);
            Assert.Equal(ConnectionStatus.Connected, (await brain.Get<IConnection>(Key).Probe()).Status);
            Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey);
            await using var changed = await brain.Observe<PullRequestChanged>(brain.Get<IRepository>(Key), ct);
            var body = System.Text.Encoding.UTF8.GetBytes("""{"action":"opened","number":4,"installation":{"id":9},"repository":{"id":11,"name":"digitalbrain","owner":{"login":"intochat"}}}""");
            Assert.Equal(GitHubWebhookAcceptance.Accepted, await ingress.HandleAsync(body, Headers(body, "pull_request", "0123456789abcdef"), ct));
            Assert.Equal(4, (await changed.NextAsync(ct: ct)).Number);
        }
        finally { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
    }

    [Fact]
    public async Task ADisconnectLeavesTheIndexAndForgetsTheInstallationToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var verifier = new FakeInstallationVerifier();
        await using var brain = await StartAsync(ct, verifier: verifier);
        try
        {
            await ConnectAsync(brain);
            CallerContextStamper.Stamp(Owner);
            await brain.Get<IConnection>(Key).Disconnect();
            Assert.Empty(await brain.Grains.GetGrain<IGitHubRepositoryIndex>("installation/9").Read());
            Assert.Equal([Key], verifier.Forgotten);
        }
        finally { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
    }

    [Fact]
    public async Task OneConnectionsDeniedAccessMarksItFailingAndTheFanOutStillReachesTheOther()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct, new DenyingSource(Key));
        try
        {
            await ConnectAsync(brain);
            await ConnectAsync(brain, Other, OtherKey);
            Assert.Equal([Key, OtherKey], await brain.Grains.GetGrain<IGitHubRepositoryIndex>("installation/9").Read());
            await using var failing = await brain.Observe<ConnectionFailing>(brain.Get<IRepository>(Key), ct);
            await using var changed = await brain.Observe<PullRequestChanged>(brain.Get<IRepository>(OtherKey), ct);
            var body = System.Text.Encoding.UTF8.GetBytes("""{"action":"opened","number":3,"installation":{"id":9},"repository":{"id":11,"name":"digitalbrain","owner":{"login":"intochat"}}}""");
            Assert.Equal(GitHubWebhookAcceptance.Accepted, await brain.SiloServices.GetRequiredService<GitHubWebhookIngress>().HandleAsync(body, Headers(body, "pull_request", "0123456789abcdef"), ct));
            Assert.Equal(3, (await changed.NextAsync(ct: ct)).Number);
            Assert.Equal(DenyingSource.Reason, (await failing.NextAsync(ct: ct)).Detail);
            Assert.Equal(ConnectionStatus.Failing, (await brain.Get<IConnection>(Key).Read()).Status);
        }
        finally { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
    }

    [Fact]
    public async Task TheInboundFaceRefusesAnotherBrainsCallerAndForeignCoordinates()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        try
        {
            await ConnectAsync(brain);
            var inbound = brain.Grains.GetConnectionGrain<IGitHubInbound>(Key);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inbound.AcceptRepositoryEvent(new(Guid.NewGuid().ToString(), "pull_request", "opened", 1, "hash", 99, 11, null), ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inbound.AcceptRepositoryEvent(new(Guid.NewGuid().ToString(), "pull_request", "opened", 1, "hash", 9, 12, null), ct));
            CallerContextStamper.Stamp(Other);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inbound.AcceptRepositoryEvent(new(Guid.NewGuid().ToString(), "installation", "revoked", null, "hash", 9, null, null), ct));
            CallerContextStamper.Stamp(Owner);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inbound.Revoke("a user asked"));
            Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey);
            Assert.True((await brain.Get<IRepository>(Key).Read()).Connected);
        }
        finally { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
    }

    [Fact]
    public async Task AGrantForARepositoryTheAppCannotReachIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        CallerContextStamper.Stamp(Owner);
        try
        {
            await brain.AuthorizeCallerAsync();
            await Assert.ThrowsAsync<GitHubUnavailableException>(() => brain.Grains.GetConnectionInbound(Key).AcceptGrant(new("oauth", "user-token", null, null, "", "https://github.com/intochat/elsewhere", "{}")));
            Assert.Equal(ConnectionStatus.Disconnected, (await brain.Get<IConnection>(Key).Read()).Status);
        }
        finally { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
    }

    private static Dictionary<string, string[]> Headers(byte[] body, string eventName, string secret, bool sameDelivery = false) => new()
    {
        ["X-GitHub-Delivery"] = [sameDelivery ? "11111111-1111-1111-1111-111111111111" : Guid.NewGuid().ToString()],
        ["X-GitHub-Event"] = [eventName],
        ["X-Hub-Signature-256"] = ["sha256=" + Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret), body))],
    };

    private sealed class FakeUserAccess : IGitHubUserAccess
    {
        public Task<GitHubRepository> ResolveAsync(string userToken, long appId, string repositoryUrl, CancellationToken ct)
            => repositoryUrl.EndsWith("/digitalbrain", StringComparison.Ordinal)
                ? Task.FromResult(new GitHubRepository("", appId, 9, 11, "intochat", "digitalbrain"))
                : throw new GitHubUnavailableException("The user and GitHub App must both have access to the requested repository.");
    }

    private sealed class FakeInstallationVerifier : IGitHubInstallationVerifier
    {
        public List<string> Forgotten { get; } = [];
        public Task VerifyAsync(GitHubRepository repository, CancellationToken ct) => Task.CompletedTask;
        public void Forget(string connectionKey) { lock (Forgotten) { Forgotten.Add(connectionKey); } }
    }

    private sealed class DenyingSource(string denied) : IGitHubRepositorySource
    {
        public const string Reason = "GitHub App authentication was refused.";
        private readonly FakeGitHubRepositorySource _inner = new();

        public Task<PullRequestSnapshot> GetPullRequestAsync(GitHubRepository repository, int number, CancellationToken cancellationToken)
            => repository.Key == denied ? throw new GitHubAccessDeniedException(Reason) : _inner.GetPullRequestAsync(repository, number, cancellationToken);

        public Task<IReadOnlyList<PullRequestSnapshot>> ListOpenPullRequestsAsync(GitHubRepository repository, CancellationToken cancellationToken)
            => repository.Key == denied ? throw new GitHubAccessDeniedException(Reason) : _inner.ListOpenPullRequestsAsync(repository, cancellationToken);

        public Task<GitHubReviewEvidence> GetReviewEvidenceAsync(GitHubRepository repository, PullRequestSnapshot snapshot, CancellationToken cancellationToken)
            => _inner.GetReviewEvidenceAsync(repository, snapshot, cancellationToken);
    }
}
