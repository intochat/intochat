using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Limits;
using DigitalBrain.Access;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Microsoft.GitHub.Signals;

namespace IntoChat.OS.Modules.Microsoft.GitHub;

// One repository connection, keyed {brain}/connection/github/{id}. The user's OAuth token proves
// access once; installation tokens minted from the App key do the ongoing work.
[DigitalBrain.Neurons.Neuron("github", State = "github")]
internal sealed class GitHubRepositoryNeuron : ConnectionNeuron<GitHubRepositoryState>, IRepository, IGitHubInbound
{
    private const int MaxProcessedDeliveries = 512;
    private const int MaxPullRequests = 512;
    private const string RevokedDetail = "GitHub revoked access to the repository. Reconnect GitHub.";

    private readonly IGitHubRepositorySource _source;
    private readonly IGitHubUserAccess _access;
    private readonly IGitHubInstallationVerifier _verifier;
    private readonly GitHubAppRegistration _registration;

    public GitHubRepositoryNeuron(IGitHubRepositorySource source, IGitHubUserAccess access, IGitHubInstallationVerifier verifier,
        GitHubAppRegistration registration)
    {
        _source = source;
        _access = access;
        _verifier = verifier;
        _registration = registration;
    }

    private GitHubRepository Repository => new(FullKey, Snapshot.AppId, Snapshot.InstallationId, Snapshot.RepositoryId, Snapshot.Owner, Snapshot.Name);
    private DigitalBrain.Limits.IPolicies Policies => Context.Get<DigitalBrain.Limits.IPolicies>(DigitalBrain.NeuronId.Parse(DigitalBrain.Limits.IPolicies.Key(Id.Brain.Value)));
    private string RepositoryFact => $"{Snapshot.Owner}/{Snapshot.Name}";

    // The installation index is a plain neuron in the system brain (06-06d); this connection is still a grain.
    private IGitHubRepositoryIndex Index(long installationId)
        => SiloNeurons.Get<IGitHubRepositoryIndex>(ServiceProvider, IGitHubRepositoryIndex.Key(installationId));

    protected override async Task<OAuthGrant> AcceptGrantAsync(OAuthGrant grant)
    {
        var appId = await _registration.ReadAppIdAsync(CancellationToken.None) ?? throw new GitHubUnavailableException("GitHub App integration is unavailable.");
        var target = grant.Target ?? throw new GitHubUnavailableException("Choose one GitHub repository.");
        var resolved = (await _access.ResolveAsync(grant.AccessToken, appId, target, CancellationToken.None)) with { Key = FullKey };
        await _verifier.VerifyAsync(resolved, CancellationToken.None);
        if (Snapshot.InstallationId != 0 && Snapshot.InstallationId != resolved.InstallationId)
        {
            var edge = ServiceProvider?.GetService(typeof(DigitalBrain.Access.EdgeAuthority)) as DigitalBrain.Access.EdgeAuthority;
            using (edge?.Enter(edge.Module("github", new BrainId("system"))))
            {
                await Index(Snapshot.InstallationId).Remove(FullKey);
            }
        }
        var account = $"{resolved.Owner}/{resolved.Name}";
        var next = (Snapshot.RepositoryId != resolved.RepositoryId
            ? Snapshot with { PullRequests = [], LastWebhookAt = null }
            : Snapshot) with
        {
            AppId = resolved.AppId,
            InstallationId = resolved.InstallationId,
            RepositoryId = resolved.RepositoryId,
            Owner = resolved.Owner,
            Name = resolved.Name,
            Account = account,
        };
        await SaveQuietly(next);
        // The user token proved access once; installation tokens do the ongoing work.
        return grant with { AccessToken = "installation", RefreshToken = null, ExpiresAt = null };
    }

    protected override async Task OnConnectedAsync()
    {
        var edge = ServiceProvider?.GetService(typeof(DigitalBrain.Access.EdgeAuthority)) as DigitalBrain.Access.EdgeAuthority;
        using (edge?.Enter(edge.Module("github", new BrainId("system"))))
        {
            await Index(Snapshot.InstallationId).Add(FullKey);
        }
    }

    protected override async Task OnDisconnectedAsync()
    {
        _verifier.Forget(FullKey);
        if (Snapshot.InstallationId == 0) { return; }
        var edge = ServiceProvider?.GetService(typeof(DigitalBrain.Access.EdgeAuthority)) as DigitalBrain.Access.EdgeAuthority;
        using (edge?.Enter(edge.Module("github", new BrainId("system"))))
        {
            await Index(Snapshot.InstallationId).Remove(FullKey);
        }
    }

    protected override async Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct)
    {
        try
        {
            await _verifier.VerifyAsync(Repository, ct);
            return ConnectionProbeResult.Connected();
        }
        catch (GitHubAccessDeniedException error) { return ConnectionProbeResult.Failing(error.Message); }
    }

    protected override Task<OAuthGrant> RefreshAsync(Credential credential, CancellationToken ct)
        => throw new InvalidOperationException("GitHub connections mint installation tokens; nothing refreshes.");

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

    // Webhook deliveries arrive unstamped once the ingress verified their signature; the neuron acts
    // only on a delivery whose routing hints name its own installation and repository.
    public async Task<bool> AcceptRepositoryEvent(RepositoryEvent receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt.DeliveryId);
        RequireOwnBrain();
        if (Snapshot.Status != ConnectionStatus.Connected) { return false; }
        if (receipt.Installation != Snapshot.InstallationId
            || receipt.Repository is { } repository && repository != Snapshot.RepositoryId
            || receipt.App is { } app && app != Snapshot.AppId)
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
        RequireAvailable();
        return new(await _source.GetPullRequestAsync(Repository, query.Number, cancellationToken).ConfigureAwait(true));
    }

    public async Task<ReviewEvidenceRead> ReadReviewEvidence(ReadReviewEvidence query, CancellationToken cancellationToken = default)
    {
        RequireAvailable();
        var repository = Repository;
        var current = await _source.GetPullRequestAsync(repository, query.Expected.Number, cancellationToken).ConfigureAwait(true);
        if (current.Revision != query.Expected.Revision || current.CiRevision != query.Expected.CiRevision)
        {
            return new(current, null, "The PR or CI changed; use the current revision.");
        }

        var data = await _source.GetReviewEvidenceAsync(repository, current, cancellationToken).ConfigureAwait(true);
        var confirmation = await _source.GetPullRequestAsync(repository, current.Number, cancellationToken).ConfigureAwait(true);
        return confirmation.Revision == current.Revision && confirmation.CiRevision == current.CiRevision && data.Complete
            ? new(confirmation, data)
            : new(confirmation, null, "The evidence changed during retrieval or was incomplete.");
    }

    public Task<RequiredChecksRead> ReadRequiredChecks(CancellationToken cancellationToken = default)
    {
        RequireAvailable();
        return _source.GetRequiredChecksAsync(Repository, null, cancellationToken);
    }

    public async Task PushBranch(PushBranchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireAvailable();
        // GitHub reports acceptance in the response, so Guard's "a throw is not dispatched" reading holds for every write here.
        await Policies.Guard("github.branch.push", 1m, new Dictionary<string, string>
        {
            ["Repository"] = RepositoryFact,
            ["Branch"] = request.Branch,
        }, ct => _source.PushBranchAsync(Repository, request, ct), CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<PullRequestView> CreatePullRequest(CreatePullRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireAvailable();
        return await Policies.Guard("github.pull-request.create", 1m, new Dictionary<string, string>
        {
            ["Repository"] = RepositoryFact,
            ["Branch"] = request.HeadBranch,
        }, ct => _source.CreatePullRequestAsync(Repository, request, ct), CancellationToken.None).ConfigureAwait(false);
    }

    public async Task ReplyToReviewComment(ReplyReviewComment request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireAvailable();
        await Policies.Guard("github.review.reply", 1m, new Dictionary<string, string>
        {
            ["Repository"] = RepositoryFact,
            ["Number"] = request.PullRequestNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }, ct => _source.ReplyToReviewCommentAsync(Repository, request, ct), CancellationToken.None).ConfigureAwait(false);
    }

    public async Task MergePullRequest(MergePullRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireAvailable();
        RequireMergeProvenance();

        var current = await _source.GetPullRequestAsync(Repository, request.PullRequestNumber, CancellationToken.None).ConfigureAwait(false);
        if (!string.Equals(current.HeadSha, request.ExpectedHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Remote branch HEAD SHA '{current.HeadSha}' does not match expected HEAD SHA '{request.ExpectedHeadSha}'.");
        }

        await Policies.Guard("github.pull-request.merge", 1m, new Dictionary<string, string>
        {
            ["Repository"] = RepositoryFact,
            ["Number"] = request.PullRequestNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }, ct => _source.MergePullRequestAsync(Repository, request, ct), CancellationToken.None).ConfigureAwait(false);
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
        _verifier.Forget(FullKey);
        return MarkFailing(reason);
    }

    private async Task ApplyAsync(int? number, RepositoryEvent? trigger, string[] deliveries, DateTimeOffset? webhookAt, CancellationToken cancellationToken)
    {
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

    private async Task<IReadOnlyList<PullRequestSnapshot>> RefreshAsync(int? number, CancellationToken token)
    {
        var repository = Repository;
        if (number is { } selected)
        {
            return [await _source.GetPullRequestAsync(repository, selected, token).ConfigureAwait(true)];
        }

        var open = await _source.ListOpenPullRequestsAsync(repository, token).ConfigureAwait(true);
        var all = open.ToList();
        foreach (var old in Snapshot.PullRequests.Where(item => item.IsOpen && !open.Any(current => current.Number == item.Number)))
        {
            all.Add(await _source.GetPullRequestAsync(repository, old.Number, token).ConfigureAwait(true));
        }

        return all;
    }

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

        if (change != 0)
        {
            facts.Add(new(snapshot.Number, $"{snapshot.Revision}:{snapshot.CiRevision}", $"github:{snapshot.RepositoryId}:{snapshot.Number}"));
        }

        return null;
    }

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
