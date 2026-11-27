using System.Globalization;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Catalog;
using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Limits;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Microsoft.GitHub.Signals;
using IntoChat.OS.Modules.Microsoft.GitHub.Webhook;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Microsoft.GitHub;

// One repository connection, keyed {brain}/connection/github/{id}. Supports three auth modes:
// Fine-Grained PAT ("pat"), user OAuth token ("user-oauth" when no GitHub App is registered),
// and GitHub App installation tokens ("app").
[DigitalBrain.Neurons.Neuron("github", State = "github")]
internal sealed partial class GitHubRepositoryNeuron : ConnectionNeuron<GitHubRepositoryState>, IRepository, IGitHubInbound
{
    internal const int MaxProcessedDeliveries = 512;
    internal const int MaxPullRequests = 512;
    internal const int MaxIssues = 256;
    internal const int MaxDraftPreviews = 64;
    internal const int MaxProcessedCommentReceipts = 256;
    internal const string WebhookSecretName = "webhook";
    private const string ValueSecretName = "value";
    internal const string PollTimerName = "github.poll";
    internal const string GitHubMcpUrl = "https://api.githubcopilot.com/mcp/";
    internal const string GitHubMcpTitle = "GitHub MCP";
    internal const string GitHubMcpConnectionKey = "connection/mcp/github";
    private static readonly TimeSpan DraftPreviewLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(60);
    private const string RevokedDetail = "GitHub revoked access to the repository. Reconnect GitHub.";

    private readonly IGitHubRepositorySource _source;
    private readonly IGitHubUserAccess _access;
    private readonly IGitHubInstallationVerifier _verifier;
    private readonly GitHubAppRegistration _registration;
    private string? _pendingWebhookSecret;
    private string? _issuesETag;
    private string? _pullRequestsETag;

    public GitHubRepositoryNeuron(IGitHubRepositorySource source, IGitHubUserAccess access, IGitHubInstallationVerifier verifier,
        GitHubAppRegistration registration)
    {
        _source = source;
        _access = access;
        _verifier = verifier;
        _registration = registration;
    }

    private string NormalizedAuthKind => Snapshot.AuthKind?.ToLowerInvariant() switch
    {
        "pat" => "pat",
        "user-oauth" => "user-oauth",
        _ => "app",
    };
    private bool IsPatAuth => NormalizedAuthKind == "pat";
    private bool IsDirectAuth => NormalizedAuthKind is "pat" or "user-oauth";
    private GitHubRepository Repository => new(FullKey, Snapshot.AppId, Snapshot.InstallationId, Snapshot.RepositoryId, Snapshot.Owner, Snapshot.Name, NormalizedAuthKind);
    [DigitalBrain.Slot(Key = "default")]
    private partial DigitalBrain.Limits.IPolicies Policies { get; }
    private string RepositoryFact => $"{Snapshot.Owner}/{Snapshot.Name}";

    private TimeSpan ConfiguredPollInterval
        => (ServiceProvider?.GetService(typeof(IOptions<GitHubModuleOptions>)) as IOptions<GitHubModuleOptions>)?.Value.PollInterval
            ?? DefaultPollInterval;

    protected override Task Activated(CancellationToken cancellationToken)
    {
        if (Snapshot.Status == ConnectionStatus.Connected)
        {
            StartPollTimer();
        }

        return Task.CompletedTask;
    }

    protected override Task OnDeactivate(DeactivationCause cause, CancellationToken cancellationToken)
    {
        StopPollTimer();
        return base.OnDeactivate(cause, cancellationToken);
    }

    protected override async Task OnReminder(string name, CancellationToken cancellationToken)
    {
        if (string.Equals(name, PollTimerName, StringComparison.Ordinal))
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(true);
            return;
        }

        await base.OnReminder(name, cancellationToken).ConfigureAwait(true);
    }

    private void StartPollTimer()
    {
        var interval = ConfiguredPollInterval;
        if (Snapshot.Status == ConnectionStatus.Connected && interval > TimeSpan.Zero)
        {
            Context.Timers.Start(PollTimerName, interval, interval);
        }
    }

    private void StopPollTimer() => Context.Timers.Stop(PollTimerName);

    // The installation/repository index is a plain neuron in the system brain (06-06d); negative ids index PAT repositories.
    private IGitHubRepositoryIndex? Index(long installationOrNegRepoId)
    {
        if (ServiceProvider?.GetService(typeof(global::Orleans.IGrainFactory)) is not null)
        {
            return SiloNeurons.Get<IGitHubRepositoryIndex>(ServiceProvider, IGitHubRepositoryIndex.Key(installationOrNegRepoId));
        }

        if (ServiceProvider?.GetService(typeof(Func<long, IGitHubRepositoryIndex>)) is Func<long, IGitHubRepositoryIndex> resolver)
        {
            return resolver(installationOrNegRepoId);
        }

        if (ServiceProvider?.GetService(typeof(INeuronProxies)) is INeuronProxies proxies)
        {
            return proxies.Get<IGitHubRepositoryIndex>(SiloNeurons.IdOf(typeof(IGitHubRepositoryIndex), IGitHubRepositoryIndex.Key(installationOrNegRepoId)));
        }

        return null;
    }

    protected override async Task<OAuthGrant> AcceptGrantAsync(OAuthGrant grant)
    {
        _pendingWebhookSecret = null;

        if (IsPersonalAccessTokenGrant(grant))
        {
            var patTarget = grant.Target
                ?? TryExtractTargetFromResponse(grant.Response)
                ?? ExistingOrDerivedTarget()
                ?? throw new GitHubUnavailableException("Choose one GitHub repository.");
            var patResolved = (await _access.ResolvePersonalAccessTokenAsync(grant.AccessToken, patTarget, CancellationToken.None)) with { Key = FullKey, AuthKind = "pat" };
            using (GitHubCredentialScope.UsePersonalAccessToken(FullKey, grant.AccessToken))
            {
                await _verifier.VerifyAsync(patResolved, CancellationToken.None);
            }

            await RemoveStaleIndexEntriesAsync(nextInstallationId: 0, nextRepositoryId: patResolved.RepositoryId);
            var patAccount = $"{patResolved.Owner}/{patResolved.Name}";
            var patNext = (Snapshot.RepositoryId != patResolved.RepositoryId
                ? Snapshot with { PullRequests = [], Issues = [], DraftPreviews = [], ProcessedCommentReceipts = [], LastWebhookAt = null }
                : Snapshot) with
            {
                AuthKind = "pat",
                AppId = 0,
                InstallationId = 0,
                RepositoryId = patResolved.RepositoryId,
                Owner = patResolved.Owner,
                Name = patResolved.Name,
                Account = patAccount,
            };
            await SaveQuietly(patNext);
            _pendingWebhookSecret = TryExtractWebhookSecretFromResponse(grant.Response);
            // ConnectionNeuron seals accepted.AccessToken into State.Secrets["access"]; plaintext never touches GitHubRepositoryState.
            return grant with { RefreshToken = null };
        }

        var appId = IsUserOAuthGrant(grant)
            ? null
            : await _registration.ReadAppIdAsync(CancellationToken.None);

        if (appId is null)
        {
            var userTarget = grant.Target
                ?? TryExtractTargetFromResponse(grant.Response)
                ?? ExistingOrDerivedTarget()
                ?? throw new GitHubUnavailableException("Choose one GitHub repository.");
            var userResolved = (await _access.ResolvePersonalAccessTokenAsync(grant.AccessToken, userTarget, CancellationToken.None)) with { Key = FullKey, AuthKind = "user-oauth" };
            using (GitHubCredentialScope.UsePersonalAccessToken(FullKey, grant.AccessToken))
            {
                await _verifier.VerifyAsync(userResolved, CancellationToken.None);
            }

            await RemoveStaleIndexEntriesAsync(nextInstallationId: 0, nextRepositoryId: userResolved.RepositoryId);
            var userAccount = $"{userResolved.Owner}/{userResolved.Name}";
            var userNext = (Snapshot.RepositoryId != userResolved.RepositoryId
                ? Snapshot with { PullRequests = [], Issues = [], DraftPreviews = [], ProcessedCommentReceipts = [], LastWebhookAt = null }
                : Snapshot) with
            {
                AuthKind = "user-oauth",
                AppId = 0,
                InstallationId = 0,
                RepositoryId = userResolved.RepositoryId,
                Owner = userResolved.Owner,
                Name = userResolved.Name,
                Account = userAccount,
            };
            await SaveQuietly(userNext);
            _pendingWebhookSecret = TryExtractWebhookSecretFromResponse(grant.Response);
            // User OAuth token (gho_/ghu_) is sealed into State.Secrets["access"] and used directly as Bearer token.
            return grant with { RefreshToken = null };
        }

        var target = grant.Target ?? throw new GitHubUnavailableException("Choose one GitHub repository.");
        var resolved = (await _access.ResolveAsync(grant.AccessToken, appId.Value, target, CancellationToken.None)) with { Key = FullKey, AuthKind = "app" };
        await _verifier.VerifyAsync(resolved, CancellationToken.None);
        await RemoveStaleIndexEntriesAsync(resolved.InstallationId, resolved.RepositoryId);
        var account = $"{resolved.Owner}/{resolved.Name}";
        var next = (Snapshot.RepositoryId != resolved.RepositoryId
            ? Snapshot with { PullRequests = [], Issues = [], DraftPreviews = [], ProcessedCommentReceipts = [], LastWebhookAt = null }
            : Snapshot) with
        {
            AuthKind = "app",
            AppId = resolved.AppId,
            InstallationId = resolved.InstallationId,
            RepositoryId = resolved.RepositoryId,
            Owner = resolved.Owner,
            Name = resolved.Name,
            Account = account,
        };
        await SaveQuietly(next);
        _pendingWebhookSecret = TryExtractWebhookSecretFromResponse(grant.Response);
        // The user token proved access once; installation tokens do the ongoing work.
        return grant with { AccessToken = "installation", RefreshToken = null, ExpiresAt = null };
    }

    private async Task RemoveStaleIndexEntriesAsync(long nextInstallationId, long nextRepositoryId)
    {
        var removeOldInstallation = Snapshot.InstallationId > 0 && Snapshot.InstallationId != nextInstallationId;
        var removeOldRepoIndex = Snapshot.RepositoryId > 0 && (Snapshot.RepositoryId != nextRepositoryId || nextInstallationId > 0);
        if (!removeOldInstallation && !removeOldRepoIndex)
        {
            return;
        }

        var edge = ServiceProvider?.GetService(typeof(DigitalBrain.Access.EdgeAuthority)) as DigitalBrain.Access.EdgeAuthority;
        using (edge?.Enter(edge.Module("github", new BrainId("system"))))
        {
            if (removeOldInstallation && Index(Snapshot.InstallationId) is { } oldInstIndex)
            {
                await oldInstIndex.Remove(FullKey);
            }
            if (removeOldRepoIndex && Index(-Snapshot.RepositoryId) is { } oldRepoIndex)
            {
                await oldRepoIndex.Remove(FullKey);
            }
        }
    }

    protected override async Task OnConnectedAsync()
    {
        try
        {
            if (string.Equals(Snapshot.AuthMethodId, "pat", StringComparison.OrdinalIgnoreCase)
                && Snapshot.Secrets.ContainsKey(ValueSecretName))
            {
                await FinalizeDirectPatConnectAsync().ConfigureAwait(true);
            }
            else if (!string.IsNullOrWhiteSpace(_pendingWebhookSecret))
            {
                var sealedWebhook = await SealSecretAsync(WebhookSecretName, _pendingWebhookSecret).ConfigureAwait(true);
                var updatedSecrets = new Dictionary<string, SealedSecret>(Snapshot.Secrets, StringComparer.Ordinal)
                {
                    [WebhookSecretName] = sealedWebhook,
                };
                await SaveQuietly(Snapshot with { Secrets = updatedSecrets }).ConfigureAwait(true);
            }
        }
        finally
        {
            _pendingWebhookSecret = null;
        }

        var edge = ServiceProvider?.GetService(typeof(DigitalBrain.Access.EdgeAuthority)) as DigitalBrain.Access.EdgeAuthority;
        using (edge?.Enter(edge.Module("github", new BrainId("system"))))
        {
            if (Snapshot.InstallationId > 0 && Index(Snapshot.InstallationId) is { } instIndex)
            {
                await instIndex.Add(FullKey);
            }
            if (IsDirectAuth && Snapshot.RepositoryId > 0 && Index(-Snapshot.RepositoryId) is { } repoIndex)
            {
                await repoIndex.Add(FullKey);
            }
        }

        StartPollTimer();

        if (IsDirectAuth)
        {
            await SyncGitHubMcpAsync().ConfigureAwait(true);
        }
    }

    protected override async Task OnDisconnectedAsync()
    {
        StopPollTimer();
        _issuesETag = null;
        _pullRequestsETag = null;
        _verifier.Forget(FullKey);
        if (Snapshot.InstallationId > 0 || Snapshot.RepositoryId > 0)
        {
            var edge = ServiceProvider?.GetService(typeof(DigitalBrain.Access.EdgeAuthority)) as DigitalBrain.Access.EdgeAuthority;
            using (edge?.Enter(edge.Module("github", new BrainId("system"))))
            {
                if (Snapshot.InstallationId > 0 && Index(Snapshot.InstallationId) is { } instIndex)
                {
                    await instIndex.Remove(FullKey);
                }
                if (Snapshot.RepositoryId > 0 && Index(-Snapshot.RepositoryId) is { } repoIndex)
                {
                    await repoIndex.Remove(FullKey);
                }
            }
        }

        await DisconnectGitHubMcpIfLastAsync().ConfigureAwait(true);
    }

    protected override async Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct)
    {
        try
        {
            using var scope = IsDirectAuth ? GitHubCredentialScope.UsePersonalAccessToken(FullKey, credential.Value) : null;
            await _verifier.VerifyAsync(Repository, ct);
            return ConnectionProbeResult.Connected();
        }
        catch (GitHubAccessDeniedException error) { return ConnectionProbeResult.Failing(error.Message); }
    }

    protected override Task<OAuthGrant> RefreshAsync(Credential credential, CancellationToken ct)
        => throw new InvalidOperationException("GitHub connections mint installation tokens or use non-refreshing PATs; nothing refreshes.");

    // The inbound face: only the kernel or the GitHub module acting as itself revokes.
    public async Task Revoke(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (Context.Caller?.Actor is not (Actor.Kernel or Actor.Module { Id: GitHubModule.ManifestName }))
        { throw new UnauthorizedAccessException($"Only the platform can revoke connection github/{Key.Connection}."); }
        await RevokeAsync(reason);
    }

    [global::Orleans.Concurrency.ReadOnly]
    public Task<GitHubRepository?> Coordinates()
    {
        RequireOwnBrain();
        return Task.FromResult(Snapshot.Status == ConnectionStatus.Connected ? Repository : null);
    }

    [global::Orleans.Concurrency.ReadOnly]
    public async Task<bool> VerifyWebhookSignature(ReadOnlyMemory<byte> body, string signature)
    {
        RequireOwnBrain();
        if (Snapshot.Status != ConnectionStatus.Connected
            || string.IsNullOrWhiteSpace(signature)
            || !Snapshot.Secrets.TryGetValue(WebhookSecretName, out var sealedSecret))
        {
            return false;
        }

        var secret = await OpenSecretAsync(sealedSecret).ConfigureAwait(true);
        return secret is { Length: >= 16 } && GitHubWebhookIngress.ValidateSignature(body.Span, signature, secret);
    }

    // Webhook deliveries arrive unstamped once the ingress verified their signature; the neuron acts
    // only on a delivery whose routing hints name its own installation and repository.
    public async Task<bool> AcceptRepositoryEvent(RepositoryEvent receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt.DeliveryId);
        RequireOwnBrain();
        if (Snapshot.Status != ConnectionStatus.Connected) { return false; }
        if ((!IsDirectAuth && receipt.Installation != Snapshot.InstallationId)
            || receipt.Repository is { } repository && repository != Snapshot.RepositoryId
            || (IsDirectAuth && receipt.Repository is null && receipt.Event != "ping")
            || (!IsDirectAuth && receipt.App is { } app && app != Snapshot.AppId))
        { throw new UnauthorizedAccessException($"The delivery does not address connection github/{Key.Connection}."); }
        if (Snapshot.ProcessedDeliveries.Contains(receipt.DeliveryId, StringComparer.Ordinal)) { return false; }

        var deliveries = Snapshot.ProcessedDeliveries.Append(receipt.DeliveryId).TakeLast(MaxProcessedDeliveries).ToArray();
        if (receipt.Action == "revoked")
        {
            await SaveQuietly(Snapshot with { ProcessedDeliveries = deliveries, LastWebhookAt = Context.Time.GetUtcNow() });
            await RevokeAsync(RevokedDetail);
            return true;
        }

        try
        {
            await ApplyAsync(receipt.Number, receipt, deliveries, Context.Time.GetUtcNow(), cancellationToken);
        }
        catch (GitHubAccessDeniedException error)
        {
            // A denied installation token or vanished repository fails this connection only; the
            // delivery is recorded so a redelivery does not retry it. The message is a fixed sentence.
            await SaveQuietly(Snapshot with { ProcessedDeliveries = deliveries });
            await MarkFailing(error.Message);
        }
        return true;
    }

    public async Task<RepositoryView> Refresh(RefreshRepository request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireAvailable();
        if (request.Number is { } number)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        }

        await ApplyAsync(request.Number, trigger: null, Snapshot.ProcessedDeliveries, webhookAt: null, CancellationToken.None);
        return View();
    }

    // IConnection.Read is the base's; this is the repository face's own view.
    Task<RepositoryView> IRepository.Read() => Task.FromResult(View());

    public Task<PullRequestsRead> ReadPullRequests()
    {
        RequireAvailable();
        return Task.FromResult(new PullRequestsRead(Snapshot.PullRequests, true));
    }

    public async Task<PullRequestRead> ReadPullRequest(ReadPullRequest query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        RequireAvailable();
        var snapshot = await ExecuteWithAuthAsync((repo, ct) => _source.GetPullRequestAsync(repo, query.Number, ct), cancellationToken).ConfigureAwait(true);
        return new(snapshot);
    }

    public async Task<ReviewEvidenceRead> ReadReviewEvidence(ReadReviewEvidence query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        RequireAvailable();
        return await ExecuteWithAuthAsync(async (repository, ct) =>
        {
            var current = await _source.GetPullRequestAsync(repository, query.Expected.Number, ct).ConfigureAwait(true);
            if (current.Revision != query.Expected.Revision || current.CiRevision != query.Expected.CiRevision)
            {
                return new ReviewEvidenceRead(current, null, "The PR or CI changed; use the current revision.");
            }

            var data = await _source.GetReviewEvidenceAsync(repository, current, ct).ConfigureAwait(true);
            var confirmation = await _source.GetPullRequestAsync(repository, current.Number, ct).ConfigureAwait(true);
            return confirmation.Revision == current.Revision && confirmation.CiRevision == current.CiRevision && data.Complete
                ? new ReviewEvidenceRead(confirmation, data)
                : new ReviewEvidenceRead(confirmation, null, "The evidence changed during retrieval or was incomplete.");
        }, cancellationToken).ConfigureAwait(true);
    }

    public Task<RequiredChecksRead> ReadRequiredChecks(CancellationToken cancellationToken = default)
    {
        RequireAvailable();
        return ExecuteWithAuthAsync((repo, ct) => _source.GetRequiredChecksAsync(repo, null, ct), cancellationToken);
    }

    public Task<IssuesRead> ReadIssues()
    {
        RequireAvailable();
        return Task.FromResult(new IssuesRead(Snapshot.Issues, true));
    }

    public async Task<IssueRead> ReadIssue(ReadIssue query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Number, 1);
        RequireAvailable();
        var rawSnapshot = await ExecuteWithAuthAsync((repo, ct) => _source.GetIssueAsync(repo, query.Number, ct), cancellationToken).ConfigureAwait(true);
        var fencedBody = IssueRead.FenceUntrustedBody(Snapshot.Owner, Snapshot.Name, Snapshot.RepositoryId, rawSnapshot);
        return new IssueRead(rawSnapshot with { Body = fencedBody }, fencedBody);
    }

    public Task<IssueCommentPreview[]> ReadPendingIssueDrafts()
    {
        if (Snapshot.Status != ConnectionStatus.Connected)
        {
            return Task.FromResult<IssueCommentPreview[]>([]);
        }

        var now = Context.Time.GetUtcNow();
        var pending = Snapshot.DraftPreviews.Where(draft => draft.ExpiresAt > now).ToArray();
        return Task.FromResult(pending);
    }

    public async Task<bool> DismissIssueDraft(string previewId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireAvailable();
        if (string.IsNullOrWhiteSpace(previewId))
        {
            return false;
        }

        var normalizedId = previewId.Trim();
        var nextPreviews = Snapshot.DraftPreviews
            .Where(draft => !string.Equals(draft.PreviewId, normalizedId, StringComparison.Ordinal))
            .ToArray();
        if (nextPreviews.Length == Snapshot.DraftPreviews.Length)
        {
            return false;
        }

        await SaveQuietly(Snapshot with { DraftPreviews = nextPreviews });
        return true;
    }

    public async Task<IssueCommentPreview> PreviewIssueComment(PreviewIssueCommentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.IssueNumber, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Body);
        cancellationToken.ThrowIfCancellationRequested();
        RequireAvailable();

        var now = Context.Time.GetUtcNow();
        var retained = Snapshot.DraftPreviews
            .Where(draft => draft.ExpiresAt > now)
            .OrderBy(static draft => draft.CreatedAt ?? DateTimeOffset.MinValue)
            .TakeLast(MaxDraftPreviews - 1)
            .ToArray();
        var normalizedLabels = NormalizeLabels(request.SuggestedLabels);
        var preview = new IssueCommentPreview(
            PreviewId: Guid.NewGuid().ToString("N"),
            IssueNumber: request.IssueNumber,
            Body: request.Body.Trim(),
            SuggestedLabels: normalizedLabels,
            ExpiresAt: now.Add(DraftPreviewLifetime),
            Repository: RepositoryFact,
            CreatedAt: now);

        await SaveQuietly(Snapshot with { DraftPreviews = [.. retained, preview] });
        return preview;
    }

    public async Task<IssueCommentResult> CommentOnIssue(CommentOnIssueRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        RequireAvailable();

        var now = Context.Time.GetUtcNow();
        var hasPreview = !string.IsNullOrWhiteSpace(request.PreviewId);
        var effectiveRequestId = !string.IsNullOrWhiteSpace(request.RequestId)
            ? request.RequestId.Trim()
            : hasPreview
                ? $"preview:{request.PreviewId!.Trim()}"
                : Guid.NewGuid().ToString("N");

        if (!string.IsNullOrWhiteSpace(request.RequestId) || hasPreview)
        {
            var existingReceipt = Snapshot.ProcessedCommentReceipts.FirstOrDefault(receipt =>
                string.Equals(receipt.RequestId, effectiveRequestId, StringComparison.Ordinal)
                && (request.IssueNumber <= 0 || receipt.IssueNumber == request.IssueNumber));
            if (existingReceipt is not null)
            {
                return new IssueCommentResult(
                    existingReceipt.IssueNumber,
                    existingReceipt.CommentId,
                    existingReceipt.CommentUrl,
                    existingReceipt.AppliedLabels,
                    existingReceipt.RequestId,
                    Deduplicated: true);
            }
        }

        int effectiveIssueNumber;
        string effectiveBody;
        string[] effectiveLabels;
        string? consumedPreviewId = null;

        if (hasPreview)
        {
            var previewId = request.PreviewId!.Trim();
            var preview = Snapshot.DraftPreviews.FirstOrDefault(candidate => string.Equals(candidate.PreviewId, previewId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"Issue comment preview '{previewId}' was not found.");
            if (preview.ExpiresAt <= now)
            {
                throw new InvalidOperationException($"Issue comment preview '{previewId}' has expired.");
            }
            if (request.IssueNumber > 0 && request.IssueNumber != preview.IssueNumber)
            {
                throw new InvalidOperationException($"Issue comment preview '{previewId}' belongs to issue #{preview.IssueNumber}, not #{request.IssueNumber}.");
            }

            effectiveIssueNumber = preview.IssueNumber;
            effectiveBody = !string.IsNullOrWhiteSpace(request.Body) ? request.Body.Trim() : preview.Body;
            effectiveLabels = request.Labels is not null ? NormalizeLabels(request.Labels) : preview.SuggestedLabels;
            consumedPreviewId = previewId;
        }
        else
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(request.IssueNumber, 1);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Body);
            effectiveIssueNumber = request.IssueNumber;
            effectiveBody = request.Body.Trim();
            effectiveLabels = NormalizeLabels(request.Labels);
        }

        var commentFacts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Repository"] = RepositoryFact,
            ["Number"] = effectiveIssueNumber.ToString(CultureInfo.InvariantCulture),
        };

        var posted = await ExecuteWithAuthAsync((repository, outerCt) =>
            Policies.Guard("github.issue.comment", 1m, commentFacts, async ct =>
            {
                if (effectiveLabels.Length > 0)
                {
                    var labelFacts = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Repository"] = RepositoryFact,
                        ["Number"] = effectiveIssueNumber.ToString(CultureInfo.InvariantCulture),
                        ["Labels"] = string.Join(",", effectiveLabels),
                    };
                    return await Policies.Guard(
                        "github.issue.label",
                        1m,
                        labelFacts,
                        labelCt => _source.CommentOnIssueAsync(repository, effectiveIssueNumber, effectiveBody, effectiveLabels, effectiveRequestId, labelCt),
                        ct).ConfigureAwait(true);
                }

                return await _source.CommentOnIssueAsync(repository, effectiveIssueNumber, effectiveBody, effectiveLabels, effectiveRequestId, ct).ConfigureAwait(true);
            }, outerCt), cancellationToken).ConfigureAwait(true);

        var receiptEntry = new IssueCommentReceipt(
            posted.IssueNumber,
            effectiveRequestId,
            posted.CommentId,
            posted.CommentUrl,
            posted.AppliedLabels,
            now);
        var nextPreviews = Snapshot.DraftPreviews
            .Where(draft => draft.ExpiresAt > now && (consumedPreviewId is null || !string.Equals(draft.PreviewId, consumedPreviewId, StringComparison.Ordinal)))
            .ToArray();
        var nextReceipts = Snapshot.ProcessedCommentReceipts
            .Append(receiptEntry)
            .TakeLast(MaxProcessedCommentReceipts)
            .ToArray();
        await SaveQuietly(Snapshot with
        {
            DraftPreviews = nextPreviews,
            ProcessedCommentReceipts = nextReceipts,
        });

        return posted;
    }

    public async Task<IssueCreated> CreateIssue(CreateIssueRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);
        cancellationToken.ThrowIfCancellationRequested();
        RequireAvailable();

        var facts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Repository"] = RepositoryFact,
            ["Title"] = request.Title.Trim(),
        };

        return await ExecuteWithAuthAsync((repository, outerCt) =>
            Policies.Guard(
                "github.issue.create",
                1m,
                facts,
                ct => _source.CreateIssueAsync(repository, request, ct),
                outerCt),
            cancellationToken).ConfigureAwait(true);
    }

    public async Task PushBranch(PushBranchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireAvailable();
        // GitHub reports acceptance in the response, so Guard's "a throw is not dispatched" reading holds for every write here.
        await ExecuteWithAuthAsync((repository, outerCt) => Policies.Guard("github.branch.push", 1m, new Dictionary<string, string>
        {
            ["Repository"] = RepositoryFact,
            ["Branch"] = request.Branch,
        }, ct => _source.PushBranchAsync(repository, request, ct), outerCt), CancellationToken.None).ConfigureAwait(true);
    }

    public async Task<PullRequestView> CreatePullRequest(CreatePullRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireAvailable();
        return await ExecuteWithAuthAsync((repository, outerCt) => Policies.Guard("github.pull-request.create", 1m, new Dictionary<string, string>
        {
            ["Repository"] = RepositoryFact,
            ["Branch"] = request.HeadBranch,
        }, ct => _source.CreatePullRequestAsync(repository, request, ct), outerCt), CancellationToken.None).ConfigureAwait(true);
    }

    public async Task ReplyToReviewComment(ReplyReviewComment request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireAvailable();
        await ExecuteWithAuthAsync((repository, outerCt) => Policies.Guard("github.review.reply", 1m, new Dictionary<string, string>
        {
            ["Repository"] = RepositoryFact,
            ["Number"] = request.PullRequestNumber.ToString(CultureInfo.InvariantCulture),
        }, ct => _source.ReplyToReviewCommentAsync(repository, request, ct), outerCt), CancellationToken.None).ConfigureAwait(true);
    }

    public async Task<ReviewSubmitted> SubmitReview(SubmitReviewRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Number, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.HeadSha);
        ct.ThrowIfCancellationRequested();
        RequireAvailable();

        return await ExecuteWithAuthAsync((repository, outerCt) => Policies.Guard("github.review.submit", 1m, new Dictionary<string, string>
        {
            ["Repository"] = RepositoryFact,
            ["Number"] = request.Number.ToString(CultureInfo.InvariantCulture),
            ["Verdict"] = request.Verdict.ToString(),
        }, guardCt => _source.SubmitReviewAsync(repository, request, guardCt), outerCt), ct).ConfigureAwait(true);
    }

    public async Task MergePullRequest(MergePullRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireAvailable();
        RequireMergeProvenance();

        await ExecuteWithAuthAsync(async (repository, outerCt) =>
        {
            var current = await _source.GetPullRequestAsync(repository, request.PullRequestNumber, outerCt).ConfigureAwait(true);
            if (!string.Equals(current.HeadSha, request.ExpectedHeadSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Remote branch HEAD SHA '{current.HeadSha}' does not match expected HEAD SHA '{request.ExpectedHeadSha}'.");
            }

            await Policies.Guard("github.pull-request.merge", 1m, new Dictionary<string, string>
            {
                ["Repository"] = RepositoryFact,
                ["Number"] = request.PullRequestNumber.ToString(CultureInfo.InvariantCulture),
            }, ct => _source.MergePullRequestAsync(repository, request, ct), outerCt).ConfigureAwait(true);
        }, CancellationToken.None).ConfigureAwait(true);
    }

    private void RequireMergeProvenance()
    {
        var caller = Context.Caller ?? throw new UnauthorizedAccessException("Caller is not authenticated or stamped.");

        if (caller.Actor is Actor.Person or Actor.Kernel)
        {
            return;
        }

        throw new UnauthorizedAccessException($"Caller {caller.Actor.GetType().Name} (for {CallerRules.ActsFor(caller)}) is not authorized to merge pull requests on github/{Key.Connection}.");
    }

    // The index entry stays: Coordinates() and AcceptRepositoryEvent skip a connection that is not
    // Connected, and a probe that finds access restored brings its webhooks back with it.
    private Task RevokeAsync(string reason)
    {
        StopPollTimer();
        _verifier.Forget(FullKey);
        return MarkFailing(reason);
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        if (Snapshot.Status != ConnectionStatus.Connected || Snapshot.RepositoryId <= 0)
        {
            return;
        }

        try
        {
            await PollIssuesOnceAsync(cancellationToken).ConfigureAwait(true);
            await PollPullRequestsOnceAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            GetLogger().LogWarning(error, "GitHub conditional poll failed for {ConnectionKey}.", FullKey);
        }
    }

    private async Task PollIssuesOnceAsync(CancellationToken cancellationToken)
    {
        var result = await ExecuteWithAuthAsync(
            (repo, ct) => _source.ListOpenIssuesConditionalAsync(repo, _issuesETag, ct),
            cancellationToken).ConfigureAwait(true);
        if (result.NotModified)
        {
            if (!string.IsNullOrWhiteSpace(result.ETag))
            {
                _issuesETag = result.ETag;
            }
            return;
        }

        _issuesETag = result.ETag;
        var observedIssues = Snapshot.Issues.ToDictionary(static item => item.Number);
        var signals = new List<IssueChanged>();
        var changed = false;

        foreach (var rawIssue in result.Items)
        {
            if (rawIssue.RepositoryId != Snapshot.RepositoryId || rawIssue.Number <= 0)
            {
                continue;
            }

            var fencedBody = IssueRead.FenceUntrustedBody(Snapshot.Owner, Snapshot.Name, Snapshot.RepositoryId, rawIssue);
            var fencedIssue = rawIssue with { Body = fencedBody };
            var previous = observedIssues.GetValueOrDefault(fencedIssue.Number);
            if (previous is null && observedIssues.Count >= MaxIssues)
            {
                var evictionCandidate = observedIssues.Values.Where(static item => !item.IsOpen).OrderBy(static item => item.ObservedAt).FirstOrDefault()
                    ?? observedIssues.Values.OrderBy(static item => item.ObservedAt).First();
                observedIssues.Remove(evictionCandidate.Number);
            }

            if (previous is null || !string.Equals(previous.Revision, fencedIssue.Revision, StringComparison.Ordinal))
            {
                var action = previous is null ? "opened" : "edited";
                observedIssues[fencedIssue.Number] = fencedIssue;
                changed = true;
                signals.Add(new IssueChanged(
                    fencedIssue.Number,
                    action,
                    fencedIssue.Revision,
                    string.Create(CultureInfo.InvariantCulture, $"github:{Snapshot.RepositoryId}:issue:{fencedIssue.Number}")));
            }
        }

        if (!changed)
        {
            return;
        }

        await SaveQuietly(Snapshot with
        {
            Issues = observedIssues.Values.OrderBy(static item => item.Number).ToArray(),
        }).ConfigureAwait(true);

        foreach (var signal in signals)
        {
            await PublishAsync(signal).ConfigureAwait(true);
        }
    }

    private async Task PollPullRequestsOnceAsync(CancellationToken cancellationToken)
    {
        var result = await ExecuteWithAuthAsync(
            (repo, ct) => _source.ListOpenPullRequestsConditionalAsync(repo, _pullRequestsETag, ct),
            cancellationToken).ConfigureAwait(true);
        if (result.NotModified)
        {
            if (!string.IsNullOrWhiteSpace(result.ETag))
            {
                _pullRequestsETag = result.ETag;
            }
            return;
        }

        _pullRequestsETag = result.ETag;
        var observed = Snapshot.PullRequests.ToDictionary(static item => item.Number);
        var facts = new List<PullRequestChanged>();

        foreach (var snapshot in result.Items)
        {
            if (Apply(observed, snapshot, facts, trigger: null) is { } reason)
            {
                await MarkFailing(reason).ConfigureAwait(true);
                return;
            }
        }

        if (facts.Count == 0 && observed.Count == Snapshot.PullRequests.Length)
        {
            return;
        }

        await SaveQuietly(Snapshot with
        {
            PullRequests = observed.Values.OrderBy(static item => item.Number).ToArray(),
        }).ConfigureAwait(true);

        foreach (var fact in facts)
        {
            await PublishAsync(fact).ConfigureAwait(true);
        }
    }

    private async Task ApplyAsync(int? number, RepositoryEvent? trigger, string[] deliveries, DateTimeOffset? webhookAt, CancellationToken cancellationToken)
    {
        if (trigger?.Event is "issues" or "issue_comment")
        {
            await ApplyIssueEventAsync(trigger, deliveries, webhookAt, cancellationToken).ConfigureAwait(true);
            return;
        }

        var observed = Snapshot.PullRequests.ToDictionary(item => item.Number);
        var facts = new List<PullRequestChanged>();
        if (trigger?.Event != "ping")
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            foreach (var snapshot in await RefreshAsync(number, deadline.Token))
            {
                if (Apply(observed, snapshot, facts, trigger) is { } reason)
                {
                    await SaveQuietly(Snapshot with { ProcessedDeliveries = deliveries });
                    await MarkFailing(reason);
                    return;
                }
            }
        }

        await SaveQuietly(Snapshot with
        {
            PullRequests = observed.Values.OrderBy(item => item.Number).ToArray(),
            ProcessedDeliveries = deliveries,
            LastWebhookAt = webhookAt ?? Snapshot.LastWebhookAt,
        });
        foreach (var fact in facts)
        {
            await PublishAsync(fact);
        }
    }

    private async Task ApplyIssueEventAsync(RepositoryEvent trigger, string[] deliveries, DateTimeOffset? webhookAt, CancellationToken cancellationToken)
    {
        if (trigger.Number is not { } issueNumber || issueNumber <= 0)
        {
            await SaveQuietly(Snapshot with
            {
                ProcessedDeliveries = deliveries,
                LastWebhookAt = webhookAt ?? Snapshot.LastWebhookAt,
            });
            return;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var issue = await ExecuteWithAuthAsync((repo, ct) => _source.GetIssueAsync(repo, issueNumber, ct), deadline.Token).ConfigureAwait(true);
        if (issue.RepositoryId != Snapshot.RepositoryId || issue.Number <= 0)
        {
            await SaveQuietly(Snapshot with { ProcessedDeliveries = deliveries });
            await MarkFailing("Observed evidence belongs to a different repository.");
            return;
        }

        var observedIssues = Snapshot.Issues.ToDictionary(item => item.Number);
        if (!observedIssues.ContainsKey(issue.Number) && observedIssues.Count >= MaxIssues)
        {
            var evictionCandidate = observedIssues.Values.Where(static item => !item.IsOpen).OrderBy(static item => item.ObservedAt).FirstOrDefault()
                ?? observedIssues.Values.OrderBy(static item => item.ObservedAt).First();
            observedIssues.Remove(evictionCandidate.Number);
        }

        observedIssues[issue.Number] = issue;
        await SaveQuietly(Snapshot with
        {
            Issues = observedIssues.Values.OrderBy(static item => item.Number).ToArray(),
            ProcessedDeliveries = deliveries,
            LastWebhookAt = webhookAt ?? Snapshot.LastWebhookAt,
        });

        var action = trigger.Action ?? (trigger.Event == "issue_comment" ? "created" : "updated");
        var version = trigger.Event == "issue_comment" && !string.IsNullOrWhiteSpace(trigger.BodyHash)
            ? $"{issue.Revision}:{trigger.BodyHash[..Math.Min(12, trigger.BodyHash.Length)]}"
            : issue.Revision;
        await PublishAsync(new IssueChanged(
            issue.Number,
            action,
            version,
            string.Create(CultureInfo.InvariantCulture, $"github:{Snapshot.RepositoryId}:issue:{issue.Number}")));
    }

    private Task<IReadOnlyList<PullRequestSnapshot>> RefreshAsync(int? number, CancellationToken token)
        => ExecuteWithAuthAsync(async (repository, ct) =>
        {
            if (number is { } selected)
            {
                return (IReadOnlyList<PullRequestSnapshot>)[await _source.GetPullRequestAsync(repository, selected, ct).ConfigureAwait(true)];
            }

            var open = await _source.ListOpenPullRequestsAsync(repository, ct).ConfigureAwait(true);
            var all = open.ToList();
            foreach (var old in Snapshot.PullRequests.Where(item => item.IsOpen && !open.Any(current => current.Number == item.Number)))
            {
                all.Add(await _source.GetPullRequestAsync(repository, old.Number, ct).ConfigureAwait(true));
            }

            return all;
        }, token);

    private string? Apply(Dictionary<int, PullRequestSnapshot> observed, PullRequestSnapshot snapshot, List<PullRequestChanged> facts, RepositoryEvent? trigger)
    {
        if (snapshot.RepositoryId != Snapshot.RepositoryId || snapshot.Number <= 0)
        {
            return "Observed evidence belongs to a different repository.";
        }

        var previous = observed.GetValueOrDefault(snapshot.Number);
        if (previous is null && observed.Count >= MaxPullRequests)
        {
            var expired = observed.Values.Where(item => !item.IsOpen).OrderBy(item => item.ObservedAt).FirstOrDefault();
            if (expired is null)
            {
                return "The repository observation capacity is full.";
            }

            observed.Remove(expired.Number);
        }

        observed[snapshot.Number] = snapshot;
        var change = (PullRequestChange)0;
        if (previous is null || !previous.IsOpen && snapshot.IsOpen)
        {
            change |= snapshot.IsOpen ? PullRequestChange.Opened : PullRequestChange.Closed;
        }
        else if (previous.IsOpen && !snapshot.IsOpen)
        {
            change |= PullRequestChange.Closed;
        }
        else if (previous.Revision != snapshot.Revision || previous.IsDraft != snapshot.IsDraft)
        {
            change |= PullRequestChange.Updated;
        }

        if (previous is not null && previous.CiRevision != snapshot.CiRevision)
        {
            change |= PullRequestChange.Checks;
        }

        if (trigger?.Number == snapshot.Number && trigger.Action is "opened" or "reopened" or "closed")
        {
            change |= trigger.Action == "closed" ? PullRequestChange.Closed : PullRequestChange.Opened;
        }
        else if (trigger?.Number == snapshot.Number && trigger.Action == "ready_for_review")
        {
            change |= PullRequestChange.Updated;
        }

        if (change != 0)
        {
            var kind = change.HasFlag(PullRequestChange.Closed)
                ? PullRequestKind.Closed
                : change.HasFlag(PullRequestChange.Opened)
                    ? PullRequestKind.Opened
                    : (trigger?.Number == snapshot.Number && trigger.Action == "ready_for_review")
                        || (previous is not null && previous.IsDraft && !snapshot.IsDraft)
                        ? PullRequestKind.ReadyForReview
                        : change.HasFlag(PullRequestChange.Updated)
                            ? PullRequestKind.Updated
                            : PullRequestKind.ChecksChanged;
            var author = !string.IsNullOrWhiteSpace(snapshot.Author)
                ? snapshot.Author
                : (trigger?.Number == snapshot.Number ? trigger.Author ?? string.Empty : string.Empty);

            facts.Add(new(
                snapshot.Number,
                $"{snapshot.Revision}:{snapshot.CiRevision}",
                $"github:{snapshot.RepositoryId}:{snapshot.Number}",
                Kind: kind,
                HeadSha: snapshot.HeadSha,
                Author: author,
                IsDraft: snapshot.IsDraft));
        }

        return null;
    }

    private Task<T> ExecuteWithAuthAsync<T>(Func<GitHubRepository, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        var repository = Repository;
        if (!repository.IsDirectToken)
        {
            return action(repository, ct);
        }

        return UseCredentialAsync(async (credential, token) =>
        {
            using var scope = GitHubCredentialScope.UsePersonalAccessToken(repository.Key, credential.Value);
            return await action(repository, token).ConfigureAwait(true);
        }, ct);
    }

    private Task ExecuteWithAuthAsync(Func<GitHubRepository, CancellationToken, Task> action, CancellationToken ct)
        => ExecuteWithAuthAsync<bool>(async (repo, token) =>
        {
            await action(repo, token).ConfigureAwait(true);
            return true;
        }, ct);

    private async Task FinalizeDirectPatConnectAsync()
    {
        try
        {
            await UseCredentialAsync<bool>(async (credential, _) =>
            {
                var (token, target, webhookSecret) = ParseDirectConnectPayload(credential.Value);
                var effectiveTarget = target
                    ?? ExistingOrDerivedTarget()
                    ?? throw new GitHubUnavailableException("Choose one GitHub repository.");

                var resolved = (await _access.ResolvePersonalAccessTokenAsync(token, effectiveTarget, CancellationToken.None).ConfigureAwait(true))
                    with
                { Key = FullKey, AuthKind = "pat" };
                using (GitHubCredentialScope.UsePersonalAccessToken(FullKey, token))
                {
                    await _verifier.VerifyAsync(resolved, CancellationToken.None).ConfigureAwait(true);
                }

                await RemoveStaleIndexEntriesAsync(nextInstallationId: 0, nextRepositoryId: resolved.RepositoryId).ConfigureAwait(true);
                var sealedToken = await SealSecretAsync(ValueSecretName, token).ConfigureAwait(true);
                var updatedSecrets = new Dictionary<string, SealedSecret>(Snapshot.Secrets, StringComparer.Ordinal)
                {
                    [ValueSecretName] = sealedToken,
                };
                if (!string.IsNullOrWhiteSpace(webhookSecret))
                {
                    updatedSecrets[WebhookSecretName] = await SealSecretAsync(WebhookSecretName, webhookSecret).ConfigureAwait(true);
                }

                var account = $"{resolved.Owner}/{resolved.Name}";
                var next = (Snapshot.RepositoryId != resolved.RepositoryId
                    ? Snapshot with { PullRequests = [], Issues = [], DraftPreviews = [], ProcessedCommentReceipts = [], LastWebhookAt = null }
                    : Snapshot) with
                {
                    AuthKind = "pat",
                    AppId = 0,
                    InstallationId = 0,
                    RepositoryId = resolved.RepositoryId,
                    Owner = resolved.Owner,
                    Name = resolved.Name,
                    Account = account,
                    Secrets = updatedSecrets,
                };
                await SaveQuietly(next).ConfigureAwait(true);
                return true;
            }, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Roll back cleanly so a failed direct PAT connect never leaves Status=Connected
            // or raw unparsed JSON/pipe payload sealed in State.Secrets["value"].
            await SaveQuietly(Snapshot with
            {
                Secrets = new Dictionary<string, SealedSecret>(StringComparer.Ordinal),
            }).ConfigureAwait(true);
            await MarkFailing(error.Message).ConfigureAwait(true);
            throw;
        }
    }

    private async Task SyncGitHubMcpAsync()
    {
        try
        {
            await UseCredentialAsync<bool>(async (credential, _) =>
            {
                if (!IsGitHubUserOrPatToken(credential.Value))
                {
                    return false;
                }

                var payload = JsonSerializer.Serialize(
                    new GitHubMcpConnectPayload(GitHubMcpUrl, GitHubMcpTitle, credential.Value),
                    GitHubAotJsonContext.Default.GitHubMcpConnectPayload);
                var mcpId = new NeuronId(Id.Brain, "mcp", GitHubMcpConnectionKey);
                await Get<IConnection>(mcpId).Connect("bearer-token", payload).ConfigureAwait(true);
                return true;
            }, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            GetLogger().LogWarning(error, "Best-effort sync of GitHub remote MCP connection failed for {ConnectionKey}.", FullKey);
        }
    }

    private async Task DisconnectGitHubMcpIfLastAsync()
    {
        try
        {
            var hasOtherConnectedGitHub = false;
            try
            {
                var catalog = await Context.Get<IConnectionCatalog>(IConnectionCatalog.Id(Key.Brain)).Read().ConfigureAwait(true);
                hasOtherConnectedGitHub = catalog.Rows.Any(row =>
                    string.Equals(row.ConnectorId, GitHubModule.ManifestName, StringComparison.Ordinal)
                    && !string.Equals(row.ConnectionId, Key.Connection, StringComparison.Ordinal)
                    && row.Status == ConnectionStatus.Connected);
            }
            catch (Exception catalogError) when (catalogError is not OperationCanceledException)
            {
                GetLogger().LogDebug(catalogError, "Connection catalog unavailable when checking remaining GitHub connections for {ConnectionKey}.", FullKey);
            }

            if (hasOtherConnectedGitHub)
            {
                return;
            }

            var mcpId = new NeuronId(Id.Brain, "mcp", GitHubMcpConnectionKey);
            await Get<IConnection>(mcpId).Disconnect().ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            GetLogger().LogDebug(error, "Best-effort disconnect of GitHub remote MCP connection skipped for {ConnectionKey}.", FullKey);
        }
    }

    private async Task<SealedSecret> SealSecretAsync(string name, string plaintext)
    {
        var brainSealer = ServiceProvider?.GetService(typeof(IBrainSecretSealer)) as IBrainSecretSealer;
        if (brainSealer is not null && Snapshot.Secrets.Values.Any(BrainSeal.IsBrainSealed))
        {
            return await brainSealer.Seal(Key.Brain, name, plaintext).ConfigureAwait(true);
        }

        var sealer = ServiceProvider?.GetService(typeof(ISecretSealer)) as ISecretSealer
            ?? throw new InvalidOperationException("No ISecretSealer service is registered.");
        return sealer.Seal(name, plaintext);
    }

    private async Task<string> OpenSecretAsync(SealedSecret secret)
    {
        var brainSealer = ServiceProvider?.GetService(typeof(IBrainSecretSealer)) as IBrainSecretSealer;
        if (BrainSeal.IsBrainSealed(secret) && brainSealer is not null)
        {
            return await brainSealer.Open(Key.Brain, secret).ConfigureAwait(true);
        }

        var sealer = ServiceProvider?.GetService(typeof(ISecretSealer)) as ISecretSealer
            ?? throw new InvalidOperationException("No ISecretSealer service is registered.");
        return sealer.Open(secret);
    }

    private string? ExistingOrDerivedTarget()
    {
        if (!string.IsNullOrWhiteSpace(Snapshot.Owner) && !string.IsNullOrWhiteSpace(Snapshot.Name))
        {
            return $"{Snapshot.Owner}/{Snapshot.Name}";
        }

        var decoded = Uri.UnescapeDataString(Key.Connection);
        return decoded.Contains('/', StringComparison.Ordinal) ? decoded : null;
    }

    internal static (string Token, string? Target, string? WebhookSecret) ParseDirectConnectPayload(string rawValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawValue);
        var trimmed = rawValue.Trim();

        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var token = TryReadJsonString(doc.RootElement, "token", "access_token", "accessToken", "pat");
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        throw new GitHubUnavailableException("Provide a non-empty GitHub personal access token.");
                    }

                    var target = TryReadJsonString(doc.RootElement, "repo", "repository", "target");
                    var webhookSecret = TryReadJsonString(doc.RootElement, "webhookSecret", "webhook_secret");
                    return (token.Trim(), target?.Trim(), string.IsNullOrWhiteSpace(webhookSecret) ? null : webhookSecret.Trim());
                }
            }
            catch (JsonException)
            {
                throw new GitHubUnavailableException("Invalid JSON payload for GitHub PAT connection.");
            }
        }

        var pipeIdx = trimmed.IndexOf('|');
        if (pipeIdx > 0 && pipeIdx < trimmed.Length - 1)
        {
            var left = trimmed[..pipeIdx].Trim();
            var right = trimmed[(pipeIdx + 1)..].Trim();
            if (left.Contains('/', StringComparison.Ordinal) && right.Length > 0)
            {
                return (right, left, null);
            }
        }

        return (trimmed, null, null);
    }

    internal static bool IsGitHubUserOrPatToken(string? token)
        => !string.IsNullOrWhiteSpace(token)
            && (token.StartsWith("gho_", StringComparison.Ordinal)
                || token.StartsWith("ghu_", StringComparison.Ordinal)
                || token.StartsWith("github_pat_", StringComparison.Ordinal)
                || token.StartsWith("ghp_", StringComparison.Ordinal));

    private static bool IsUserOAuthGrant(OAuthGrant grant)
    {
        if (string.Equals(grant.AuthMethodId, "user-oauth", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(grant.Response))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(grant.Response);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var mode = TryReadJsonString(doc.RootElement, "auth_kind", "authKind", "mode");
            return string.Equals(mode, "user-oauth", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? TryExtractWebhookSecretFromResponse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var secret = TryReadJsonString(doc.RootElement, "webhookSecret", "webhook_secret");
            return string.IsNullOrWhiteSpace(secret) ? null : secret.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadJsonString(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
            {
                return val.GetString();
            }
        }

        return null;
    }

    private static bool IsPersonalAccessTokenGrant(OAuthGrant grant)
    {
        if (string.Equals(grant.AuthMethodId, "pat", StringComparison.OrdinalIgnoreCase)
            || grant.AccessToken.StartsWith("github_pat_", StringComparison.Ordinal)
            || grant.AccessToken.StartsWith("ghp_", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(grant.Response))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(grant.Response);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var key in (string[])["auth_kind", "authKind", "mode"])
            {
                if (doc.RootElement.TryGetProperty(key, out var val)
                    && val.ValueKind == JsonValueKind.String
                    && string.Equals(val.GetString(), "pat", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // Non-JSON response metadata is normal for plain grants.
            return false;
        }

        return false;
    }

    private static string? TryExtractTargetFromResponse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var key in (string[])["target", "repository", "repo"])
            {
                if (doc.RootElement.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
                {
                    return val.GetString();
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string[] NormalizeLabels(string[]? labels)
        => labels is null
            ? []
            : labels
                .Where(static label => !string.IsNullOrWhiteSpace(label))
                .Select(static label => label.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private void RequireAvailable()
    {
        if (Snapshot.Status != ConnectionStatus.Connected)
        {
            throw new GitHubAccessDeniedException("Reconnect repository access before reading evidence.");
        }
    }

    private void RequireOwnBrain()
    {
        if (Context.Caller is not { } caller
            || (caller.Actor is not Actor.Kernel && caller.Brain.Value != Key.Brain))
        { throw new UnauthorizedAccessException($"Only the owning brain can deliver to connection github/{Key.Connection}."); }
    }

    private RepositoryView View() => new(Key.Connection, Snapshot.Status == ConnectionStatus.Connected, Snapshot.PullRequests, Snapshot.LastWebhookAt);
}
