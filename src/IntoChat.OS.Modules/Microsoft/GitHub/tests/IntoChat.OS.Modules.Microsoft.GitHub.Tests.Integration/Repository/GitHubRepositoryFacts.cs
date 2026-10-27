using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Connections.Signals;
using IntoChat.OS.Modules.Microsoft.GitHub.Signals;
using IntoChat.OS.Modules.Microsoft.GitHub.Webhook;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.GitHub.Tests.Integration.Repository;

public sealed class GitHubRepositoryFacts
{
    private const string WebhookSecret = "0123456789abcdef";
    private static readonly string Key = ConnectionKeys.For(BrainScope.Create("owner", "owner").Id, "github", "intochat-os");

    private static Task<ITestBrain> StartAsync(CancellationToken ct, IGitHubRepositorySource? source = null, FakeInstallationVerifier? verifier = null) => TestBrain.Create()
        .WithModule<GitHubModule>()
        .WithIntegration(GitHubModule.Integration, "AppId", "7")
        .WithIntegration(GitHubModule.Integration, "PrivateKeyPem", "unused")
        .WithIntegration(GitHubModule.Integration, "WebhookSecret", WebhookSecret)
        .ConfigureServices(services =>
        {
            services.AddSingleton<IGitHubRepositorySource>(source ?? new FakeGitHubRepositorySource());
            services.AddSingleton<IGitHubUserAccess>(new FakeUserAccess());
            services.AddSingleton<IGitHubInstallationVerifier>(verifier ?? new FakeInstallationVerifier());
        })
        .StartAsync(ct);

    private static string KeyOf(Person person) => ConnectionKeys.For(BrainScope.Create(person.AccountId, person.BrainId).Id, "github", "intochat-os");

    private static async Task<string> ConnectAsync(ITestBrain brain, Person? person = null)
    {
        person ??= brain.Owner;
        var key = KeyOf(person);
        using (person.AsCaller())
        {
            await Plain(brain).GetConnectionInbound(key).AcceptGrant(new("oauth", "user-token", null, null, "", "https://github.com/intochat/intochat-os", "{}"));
        }
        return key;
    }

    private static IntoChat.OS.Runtime.IPlainNeurons Plain(ITestBrain brain) => brain.Services.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>();

    // The installation index is a plain neuron in the system brain (06-06d).
    private static IGitHubRepositoryIndex Index(ITestBrain brain, long installation)
        => brain.Services.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>().Get<IGitHubRepositoryIndex>(IGitHubRepositoryIndex.Key(installation));

    private static GitHubWebhookIngress Ingress(ITestBrain brain) => brain.Services.GetRequiredService<GitHubWebhookIngress>();

    private static byte[] PullRequestOpened(int number) => System.Text.Encoding.UTF8.GetBytes(
        "{\"action\":\"opened\",\"number\":" + number + ",\"installation\":{\"id\":9},\"repository\":{\"id\":11,\"name\":\"intochat-os\",\"owner\":{\"login\":\"intochat\"}}}");

    [Trait("host", "own")]
    [Fact]
    public async Task AcceptGrantResolvesTheInstallationAndConnects()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        await using var connected = await brain.Observe<ConnectionConnected>(brain.Get<IRepository>(Key), ct);
        await ConnectAsync(brain);
        Assert.True((await brain.Get<IRepository>(Key).Read()).Connected);
        Assert.Equal("intochat/intochat-os", (await connected.NextAsync(ct: ct)).Account);
        Assert.Equal([Key], await Index(brain, 9).Read());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TheUserTokenIsNeverKeptAfterTheGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        await ConnectAsync(brain);
        var view = await brain.Get<IConnection>(Key).Read();
        Assert.Equal(ConnectionStatus.Connected, view.Status);
        Assert.Null(view.ExpiresAt);
        Assert.DoesNotContain("user-token", System.Text.Json.JsonSerializer.Serialize(view), StringComparison.Ordinal);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AWebhookRoutesThroughTheRepositoryIndexToTheConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        await ConnectAsync(brain);
        await using var changed = await brain.Observe<PullRequestChanged>(brain.Get<IRepository>(Key), ct);
        var body = PullRequestOpened(1);
        Assert.Equal(GitHubWebhookAcceptance.Accepted, await Ingress(brain).HandleAsync(body, Headers(body, "pull_request", WebhookSecret, sameDelivery: true), ct));
        Assert.Equal(1, (await changed.NextAsync(ct: ct)).Number);
        Assert.Equal(GitHubWebhookAcceptance.Duplicate, await Ingress(brain).HandleAsync(body, Headers(body, "pull_request", WebhookSecret, sameDelivery: true), ct));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AWebhookWithAForgedSignatureReachesNoConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        await ConnectAsync(brain);
        var body = PullRequestOpened(1);
        Assert.Equal(GitHubWebhookAcceptance.Unauthorized, await Ingress(brain).HandleAsync(body, Headers(body, "pull_request", "not-the-webhook-secret"), ct));
        Assert.Null((await brain.Get<IRepository>(Key).Read()).LastWebhookAt);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ARestartedBrainStillRoutesWebhooksToTheRepository()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        await ConnectAsync(brain);
        await brain.RestartAsync(ct);
        await using var changed = await brain.Observe<PullRequestChanged>(brain.Get<IRepository>(Key), ct);
        var body = PullRequestOpened(2);
        Assert.Equal(GitHubWebhookAcceptance.Accepted, await Ingress(brain).HandleAsync(body, Headers(body, "pull_request", WebhookSecret), ct));
        Assert.Equal(2, (await changed.NextAsync(ct: ct)).Number);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ARevokedInstallationMarksTheConnectionFailing()
    {
        var ct = TestContext.Current.CancellationToken;
        var verifier = new FakeInstallationVerifier();
        await using var brain = await StartAsync(ct, verifier: verifier);
        await ConnectAsync(brain);
        await using var failing = await brain.Observe<ConnectionFailing>(brain.Get<IRepository>(Key), ct);
        var revoked = System.Text.Encoding.UTF8.GetBytes("""{"action":"deleted","installation":{"id":9}}""");
        await Ingress(brain).HandleAsync(revoked, Headers(revoked, "installation", WebhookSecret), ct);
        await failing.NextAsync(ct: ct);
        Assert.False((await brain.Get<IRepository>(Key).Read()).Connected);
        await Assert.ThrowsAsync<GitHubAccessDeniedException>(() => brain.Get<IRepository>(Key).ReadPullRequests());
        Assert.Equal([Key], verifier.Forgotten);
        // The index keeps the key; the status alone keeps deliveries away from a revoked connection.
        Assert.Equal([Key], await Index(brain, 9).Read());
        var delivery = PullRequestOpened(1);
        Assert.Equal(GitHubWebhookAcceptance.Ignored, await Ingress(brain).HandleAsync(delivery, Headers(delivery, "pull_request", WebhookSecret), ct));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ARevokedConnectionThatProbesHealthyReceivesWebhooksAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        await ConnectAsync(brain);
        var suspended = System.Text.Encoding.UTF8.GetBytes("""{"action":"suspend","installation":{"id":9}}""");
        await Ingress(brain).HandleAsync(suspended, Headers(suspended, "installation", WebhookSecret), ct);
        Assert.Equal(ConnectionStatus.Failing, (await brain.Get<IConnection>(Key).Read()).Status);
        using (brain.Owner.AsCaller())
        {
            Assert.Equal(ConnectionStatus.Connected, (await brain.Get<IConnection>(Key).Probe()).Status);
        }
        await using var changed = await brain.Observe<PullRequestChanged>(brain.Get<IRepository>(Key), ct);
        var body = PullRequestOpened(4);
        Assert.Equal(GitHubWebhookAcceptance.Accepted, await Ingress(brain).HandleAsync(body, Headers(body, "pull_request", WebhookSecret), ct));
        Assert.Equal(4, (await changed.NextAsync(ct: ct)).Number);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ADisconnectLeavesTheIndexAndForgetsTheInstallationToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var verifier = new FakeInstallationVerifier();
        await using var brain = await StartAsync(ct, verifier: verifier);
        await ConnectAsync(brain);
        using (brain.Owner.AsCaller())
        {
            await brain.Get<IConnection>(Key).Disconnect();
        }
        Assert.Empty(await Index(brain, 9).Read());
        Assert.Equal([Key], verifier.Forgotten);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task OneConnectionsDeniedAccessMarksItFailingAndTheFanOutStillReachesTheOther()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct, new DenyingSource(Key));
        await ConnectAsync(brain);
        var otherKey = await ConnectAsync(brain, await brain.PersonAsync("other", ct));
        Assert.Equal([Key, otherKey], await Index(brain, 9).Read());
        await using var failing = await brain.Observe<ConnectionFailing>(brain.Get<IRepository>(Key), ct);
        await using var changed = await brain.Observe<PullRequestChanged>(brain.Get<IRepository>(otherKey), ct);
        var body = PullRequestOpened(3);
        Assert.Equal(GitHubWebhookAcceptance.Accepted, await Ingress(brain).HandleAsync(body, Headers(body, "pull_request", WebhookSecret), ct));
        Assert.Equal(3, (await changed.NextAsync(ct: ct)).Number);
        Assert.Equal(DenyingSource.Reason, (await failing.NextAsync(ct: ct)).Detail);
        Assert.Equal(ConnectionStatus.Failing, (await brain.Get<IConnection>(Key).Read()).Status);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TheInboundFaceRefusesAnotherBrainsCallerAndForeignCoordinates()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        await ConnectAsync(brain);
        var other = await brain.PersonAsync("other", ct);
        var inbound = Plain(brain).Get<IGitHubInbound>(Key);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inbound.AcceptRepositoryEvent(new(Guid.NewGuid().ToString(), "pull_request", "opened", 1, "hash", 99, 11, null), ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inbound.AcceptRepositoryEvent(new(Guid.NewGuid().ToString(), "pull_request", "opened", 1, "hash", 9, 12, null), ct));
        using (other.AsCaller())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inbound.AcceptRepositoryEvent(new(Guid.NewGuid().ToString(), "installation", "revoked", null, "hash", 9, null, null), ct));
        }
        using (brain.Owner.AsCaller())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inbound.Revoke("a user asked"));
        }
        Assert.True((await brain.Get<IRepository>(Key).Read()).Connected);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AGrantForARepositoryTheAppCannotReachIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        using (brain.Owner.AsCaller())
        {
            await Assert.ThrowsAsync<GitHubUnavailableException>(() => Plain(brain).GetConnectionInbound(Key).AcceptGrant(new("oauth", "user-token", null, null, "", "https://github.com/intochat/elsewhere", "{}")));
        }
        Assert.Equal(ConnectionStatus.Disconnected, (await brain.Get<IConnection>(Key).Read()).Status);
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
            => repositoryUrl.EndsWith("/intochat-os", StringComparison.Ordinal)
                ? Task.FromResult(new GitHubRepository("", appId, 9, 11, "intochat", "intochat-os"))
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
