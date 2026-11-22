using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.Microsoft.GitHub.Webhook;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Support.Fakes.GitHub;

/// <summary>Result of delivering a signed GitHub webhook payload through <see cref="FakeGitHub.DeliverAsync"/>.</summary>
public enum WebhookDeliveryResult
{
    Accepted,
    Duplicate,
    Ignored,
    BadRequest,
    Unauthorized,
    Unavailable,
}

/// <summary>
/// In-memory <see cref="IGitHubRepositorySource"/> fake with fluent repository, issue, and pull request seeding
/// plus real HMAC-SHA256 webhook delivery through <see cref="GitHubWebhookIngress"/> on both TestOS and Silo.
/// </summary>
public sealed class FakeGitHub : IGitHubRepositorySource, IGitHubUserAccess, IGitHubInstallationVerifier, IModuleIntegrations<GitHubModule>
{
    /// <summary>Default webhook secret used when none is configured via <see cref="WebhookSecret"/>.</summary>
    public const string DefaultWebhookSecret = "fake-github-webhook-secret-0123456789";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, SeededRepository> _repositories = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RecordedIssueComment> _issueComments = [];
    private readonly List<SubmitReviewRequest> _submittedReviews = [];
    private readonly List<PushBranchRequest> _pushedBranches = [];
    private readonly List<CreatePullRequest> _createdPullRequests = [];
    private long _nextRepositoryId = 10_000;
    private long _nextInstallationId = 9_000;
    private long _nextCommentId = 1_000;
    private long _nextReviewId = 5_000;
    private long _appId = 77;
    private string _webhookSecret = DefaultWebhookSecret;

    /// <summary>Reviews submitted through <see cref="IRepository.SubmitReview"/>.</summary>
    public IReadOnlyList<SubmitReviewRequest> SubmittedReviews
    {
        get
        {
            lock (_gate)
            {
                return [.. _submittedReviews];
            }
        }
    }

    /// <summary>Branches pushed through <see cref="IRepository.PushBranch"/>.</summary>
    public IReadOnlyList<PushBranchRequest> PushedBranches
    {
        get
        {
            lock (_gate)
            {
                return [.. _pushedBranches];
            }
        }
    }

    /// <summary>Pull requests created through <see cref="IRepository.CreatePullRequest"/>.</summary>
    public IReadOnlyList<CreatePullRequest> CreatedPullRequests
    {
        get
        {
            lock (_gate)
            {
                return [.. _createdPullRequests];
            }
        }
    }

    /// <summary>Sets the GitHub App ID advertised to <see cref="GitHubAppRegistration"/>.</summary>
    public FakeGitHub AppId(long appId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(appId, 0);
        lock (_gate)
        {
            _appId = appId;
        }
        return this;
    }

    /// <summary>Sets the webhook signing secret validated by <see cref="GitHubWebhookIngress"/>.</summary>
    public FakeGitHub WebhookSecret(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        lock (_gate)
        {
            _webhookSecret = secret;
        }
        return this;
    }

    /// <summary>Seeds a repository identified by <paramref name="slug"/> (<c>owner/name</c>).</summary>
    public FakeGitHub Repository(string slug, long? repositoryId = null, long? installationId = null)
    {
        var (owner, name) = ParseSlug(slug);
        var key = $"{owner}/{name}";
        lock (_gate)
        {
            if (_repositories.TryGetValue(key, out var existing))
            {
                if (repositoryId is { } repoId) { existing.RepositoryId = repoId; }
                if (installationId is { } instId) { existing.InstallationId = instId; }
            }
            else
            {
                _repositories[key] = new SeededRepository(
                    owner,
                    name,
                    repositoryId ?? ++_nextRepositoryId,
                    installationId ?? ++_nextInstallationId);
            }
        }
        return this;
    }

    /// <summary>Seeds an issue on the repository identified by <paramref name="slug"/>.</summary>
    public FakeGitHub Issue(
        string slug,
        int number,
        string title,
        string body = "",
        bool isOpen = true,
        string author = "reporter",
        string[]? labels = null,
        int commentCount = 0,
        string? revision = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var repo = EnsureRepositoryLocked(slug);
            repo.Issues[number] = new IssueSnapshot(
                Number: number,
                Title: title,
                Body: body,
                Url: $"https://github.com/{repo.Owner}/{repo.Name}/issues/{number}",
                IsOpen: isOpen,
                Author: author,
                Labels: labels is null ? [] : [.. labels],
                CommentCount: commentCount,
                ObservedAt: now,
                CreatedAt: now,
                UpdatedAt: now,
                Revision: revision ?? $"rev-{number}",
                RepositoryId: repo.RepositoryId);
        }
        return this;
    }

    /// <summary>Seeds a pull request on the repository identified by <paramref name="slug"/>.</summary>
    public FakeGitHub PullRequest(
        string slug,
        int number,
        string headSha,
        string title = "Pull request",
        bool isOpen = true,
        bool isDraft = false,
        string? baseSha = null,
        string? baseBranch = "main",
        string? revision = null,
        string? ciRevision = null,
        string author = "",
        string? diff = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var repo = EnsureRepositoryLocked(slug);
            repo.PullRequests[number] = new PullRequestSnapshot(
                Number: number,
                Title: title,
                Url: $"https://github.com/{repo.Owner}/{repo.Name}/pull/{number}",
                IsOpen: isOpen,
                IsDraft: isDraft,
                HeadSha: headSha,
                BaseSha: baseSha ?? new string('0', 40),
                MergeSha: null,
                CiSha: headSha,
                Checks: [],
                ChecksComplete: true,
                ObservedAt: now,
                CreatedAt: now,
                Revision: revision ?? $"pr-{number}-{headSha[..Math.Min(8, headSha.Length)]}",
                CiRevision: ciRevision ?? "ci-ok",
                RepositoryId: repo.RepositoryId,
                BaseBranch: baseBranch,
                Author: author);
            if (diff is not null)
            {
                repo.PullRequestDiffs[number] = diff;
            }
        }
        return this;
    }

    /// <summary>
    /// Seeds a connected <see cref="GitHubRepositoryNeuron"/> in <paramref name="brain"/> for <paramref name="slug"/>
    /// and registers it in <see cref="IGitHubRepositoryIndex"/> on both TestOS and Silo.
    /// </summary>
    public async Task<NeuronId> ConnectAsync(
        INeuronRuntime runtime,
        BrainId brain,
        string slug,
        string connectionId = "main",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        long appId;
        long installationId;
        long repositoryId;
        string owner;
        string name;
        lock (_gate)
        {
            var repo = EnsureRepositoryLocked(slug);
            appId = _appId;
            installationId = repo.InstallationId;
            repositoryId = repo.RepositoryId;
            owner = repo.Owner;
            name = repo.Name;
        }

        var connId = new NeuronId(brain, "github", $"connection/github/{connectionId}");
        var fullKey = $"{brain.Value}/{connId.Key}";
        var stateJson = JsonSerializer.Serialize(
            new GitHubRepositoryState
            {
                Status = ConnectionStatus.Connected,
                AuthKind = "app",
                AppId = appId,
                InstallationId = installationId,
                RepositoryId = repositoryId,
                Owner = owner,
                Name = name,
                Account = $"{owner}/{name}",
            },
            RuntimeProtocol.OptionsFor(typeof(GitHubRepositoryState)));

        await runtime.SeedState(connId, stateJson, ct).ConfigureAwait(false);

        var indexId = SiloNeurons.IdOf(typeof(IGitHubRepositoryIndex), IGitHubRepositoryIndex.Key(installationId));
        using (runtime.Edge.Enter(runtime.Edge.Module(GitHubModule.ManifestName, new BrainId(IGitHubRepositoryIndex.SystemBrain))))
        {
            await runtime.Get<IGitHubRepositoryIndex>(indexId).Add(fullKey).ConfigureAwait(false);
        }

        await runtime.Idle().ConfigureAwait(false);
        return connId;
    }

    /// <summary>
    /// Signs <paramref name="payloadJson"/> with HMAC-SHA256 using <paramref name="secret"/> and delivers it
    /// through the real <see cref="GitHubWebhookIngress"/> hosted in <paramref name="runtime"/>.
    /// </summary>
    public async Task<WebhookDeliveryResult> DeliverAsync(
        INeuronRuntime runtime,
        BrainId brain,
        string eventName,
        string payloadJson,
        string secret,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(payloadJson);
        ArgumentNullException.ThrowIfNull(secret);
        _ = brain;

        var body = Encoding.UTF8.GetBytes(payloadJson);
        var signature = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-GitHub-Delivery"] = [Guid.NewGuid().ToString("D")],
            ["X-GitHub-Event"] = [eventName],
            ["X-Hub-Signature-256"] = [signature],
        };

        var ingress = runtime.Services.GetRequiredService<GitHubWebhookIngress>();
        var acceptance = await ingress.HandleAsync(body, headers, ct).ConfigureAwait(false);
        await runtime.Idle().ConfigureAwait(false);

        return acceptance switch
        {
            GitHubWebhookAcceptance.Accepted => WebhookDeliveryResult.Accepted,
            GitHubWebhookAcceptance.Duplicate => WebhookDeliveryResult.Duplicate,
            GitHubWebhookAcceptance.Ignored => WebhookDeliveryResult.Ignored,
            GitHubWebhookAcceptance.BadRequest => WebhookDeliveryResult.BadRequest,
            GitHubWebhookAcceptance.Unauthorized => WebhookDeliveryResult.Unauthorized,
            GitHubWebhookAcceptance.Unavailable => WebhookDeliveryResult.Unavailable,
            _ => throw new ArgumentOutOfRangeException(nameof(acceptance), acceptance, "Unknown webhook acceptance value."),
        };
    }

    Task<PullRequestSnapshot> IGitHubRepositorySource.GetPullRequestAsync(GitHubRepository repository, int number, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var repo = FindRepositoryLocked(repository);
            if (repo is not null && repo.PullRequests.TryGetValue(number, out var pr))
            {
                return Task.FromResult(pr);
            }
        }
        throw new GitHubUnavailableException($"Pull request #{number} was not seeded for {repository.Owner}/{repository.Name}.");
    }

    Task<IReadOnlyList<PullRequestSnapshot>> IGitHubRepositorySource.ListOpenPullRequestsAsync(GitHubRepository repository, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var repo = FindRepositoryLocked(repository);
            if (repo is null)
            {
                return Task.FromResult<IReadOnlyList<PullRequestSnapshot>>([]);
            }
            return Task.FromResult<IReadOnlyList<PullRequestSnapshot>>([.. repo.PullRequests.Values.Where(static pr => pr.IsOpen).OrderBy(static pr => pr.Number)]);
        }
    }

    Task<GitHubReviewEvidence> IGitHubRepositorySource.GetReviewEvidenceAsync(GitHubRepository repository, PullRequestSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string diffText = string.Empty;
        lock (_gate)
        {
            var repo = FindRepositoryLocked(repository);
            if (repo is not null && repo.PullRequestDiffs.TryGetValue(snapshot.Number, out var seededDiff))
            {
                diffText = seededDiff;
            }
        }

        var hash = string.IsNullOrEmpty(diffText)
            ? "empty"
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(diffText)));
        return Task.FromResult(new GitHubReviewEvidence(snapshot.HeadSha, snapshot.BaseSha, diffText, hash, Complete: true, ReviewComments: []));
    }

    Task IGitHubRepositorySource.PushBranchAsync(GitHubRepository repository, PushBranchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _pushedBranches.Add(request);
        }
        return Task.CompletedTask;
    }

    Task<PullRequestView> IGitHubRepositorySource.CreatePullRequestAsync(GitHubRepository repository, CreatePullRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var repo = EnsureRepositoryLocked($"{repository.Owner}/{repository.Name}");
            var number = repo.PullRequests.Count == 0 ? 1 : repo.PullRequests.Keys.Max() + 1;
            var sha = new string('a', 40);
            _createdPullRequests.Add(request);
            var view = new PullRequestView(
                number,
                request.Title,
                $"https://github.com/{repo.Owner}/{repo.Name}/pull/{number}",
                sha,
                new string('0', 40),
                IsOpen: true,
                IsDraft: request.Draft);
            return Task.FromResult(view);
        }
    }

    Task IGitHubRepositorySource.ReplyToReviewCommentAsync(GitHubRepository repository, ReplyReviewComment request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    Task IGitHubRepositorySource.MergePullRequestAsync(GitHubRepository repository, MergePullRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    Task<IssueSnapshot> IGitHubRepositorySource.GetIssueAsync(GitHubRepository repository, int number, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var repo = FindRepositoryLocked(repository);
            if (repo is not null && repo.Issues.TryGetValue(number, out var issue))
            {
                return Task.FromResult(issue);
            }
        }
        throw new GitHubUnavailableException($"Issue #{number} was not seeded for {repository.Owner}/{repository.Name}.");
    }

    Task<IssueCommentResult> IGitHubRepositorySource.CommentOnIssueAsync(
        GitHubRepository repository,
        int issueNumber,
        string body,
        string[] labels,
        string requestId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var id = ++_nextCommentId;
            var url = $"https://github.com/{repository.Owner}/{repository.Name}/issues/{issueNumber}#issuecomment-{id}";
            _issueComments.Add(new RecordedIssueComment($"{repository.Owner}/{repository.Name}", issueNumber, body, [.. labels], requestId));
            return Task.FromResult(new IssueCommentResult(issueNumber, id, url, [.. labels], requestId));
        }
    }

    Task<ReviewSubmitted> IGitHubRepositorySource.SubmitReviewAsync(
        GitHubRepository repository,
        SubmitReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var id = (++_nextReviewId).ToString(CultureInfo.InvariantCulture);
            _submittedReviews.Add(request);
            return Task.FromResult(new ReviewSubmitted(id, request.Number, request.HeadSha, request.Verdict));
        }
    }

    Task<GitHubRepository> IGitHubUserAccess.ResolveAsync(string userToken, long appId, string repositoryUrl, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (owner, name) = GitHubUserAccess.ParseTarget(repositoryUrl);
        lock (_gate)
        {
            var repo = EnsureRepositoryLocked($"{owner}/{name}");
            return Task.FromResult(new GitHubRepository(string.Empty, appId, repo.InstallationId, repo.RepositoryId, repo.Owner, repo.Name, "app"));
        }
    }

    Task<GitHubRepository> IGitHubUserAccess.ResolvePersonalAccessTokenAsync(string personalAccessToken, string repositoryTarget, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (owner, name) = GitHubUserAccess.ParseTarget(repositoryTarget);
        lock (_gate)
        {
            var repo = EnsureRepositoryLocked($"{owner}/{name}");
            return Task.FromResult(new GitHubRepository(string.Empty, 0, 0, repo.RepositoryId, repo.Owner, repo.Name, "pat"));
        }
    }

    Task IGitHubInstallationVerifier.VerifyAsync(GitHubRepository repository, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    void IGitHubInstallationVerifier.Forget(string connectionKey)
    {
    }

    IIntegrationHandle IModuleIntegrations<GitHubModule>.Get(string integrationId)
        => new FakeIntegrationHandle(this, integrationId);

    private SeededRepository EnsureRepositoryLocked(string slug)
    {
        var (owner, name) = ParseSlug(slug);
        var key = $"{owner}/{name}";
        if (!_repositories.TryGetValue(key, out var repo))
        {
            repo = new SeededRepository(owner, name, ++_nextRepositoryId, ++_nextInstallationId);
            _repositories[key] = repo;
        }
        return repo;
    }

    private SeededRepository? FindRepositoryLocked(GitHubRepository repository)
    {
        var key = $"{repository.Owner}/{repository.Name}";
        if (_repositories.TryGetValue(key, out var byName))
        {
            return byName;
        }
        return _repositories.Values.FirstOrDefault(r => r.RepositoryId == repository.RepositoryId);
    }

    private static (string Owner, string Name) ParseSlug(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        var parts = slug.Trim().Trim('/').Split('/');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            throw new ArgumentException($"Repository slug '{slug}' must have the form 'owner/name'.", nameof(slug));
        }
        return (parts[0], parts[1]);
    }

    private sealed class SeededRepository(string owner, string name, long repositoryId, long installationId)
    {
        public string Owner { get; } = owner;
        public string Name { get; } = name;
        public long RepositoryId { get; set; } = repositoryId;
        public long InstallationId { get; set; } = installationId;
        public Dictionary<int, IssueSnapshot> Issues { get; } = [];
        public Dictionary<int, PullRequestSnapshot> PullRequests { get; } = [];
        public Dictionary<int, string> PullRequestDiffs { get; } = [];
    }

    private sealed record RecordedIssueComment(string Repository, int IssueNumber, string Body, string[] Labels, string RequestId);

    private sealed class FakeIntegrationHandle(FakeGitHub owner, string integrationId) : IIntegrationHandle
    {
        public string IntegrationId { get; } = integrationId;

        public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long appId;
            lock (owner._gate)
            {
                appId = owner._appId;
            }
            return Task.FromResult(new IntegrationSnapshot
            {
                IntegrationId = IntegrationId,
                Status = IntegrationStatus.Ready,
                Settings = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["AppId"] = appId.ToString(CultureInfo.InvariantCulture),
                },
            });
        }

        public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (owner._gate)
            {
                if (string.Equals(field, "WebhookSecret", StringComparison.Ordinal))
                {
                    return Task.FromResult(owner._webhookSecret);
                }
            }
            throw new KeyNotFoundException($"Secret field '{field}' is not configured in FakeGitHub.");
        }

        public async Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default)
        {
            var snapshot = await ReadAsync(cancellationToken).ConfigureAwait(false);
            string secret;
            lock (owner._gate)
            {
                secret = owner._webhookSecret;
            }
            return new ReleasedIntegration
            {
                Snapshot = snapshot,
                Values = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["WebhookSecret"] = secret,
                },
            };
        }
    }
}
