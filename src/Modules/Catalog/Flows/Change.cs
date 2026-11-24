using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain.Confirmation;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;

namespace IntoChat.OS.Modules.Apps.Flows;

internal sealed record CoderEdit(
    string Summary,
    string ModelId,
    RepoPath[] TouchedFiles);

internal sealed record ChangeApproval(
    string PlanId,
    DateTimeOffset Expires,
    string? ApprovedBy = null);

internal sealed record ChangePublication(
    string? PushedRef = null,
    string? PullRequestUrl = null,
    bool PullRequestCompleted = false,
    bool Cleaned = false);

/// <summary>
/// One commit on one worktree on its way to a person's approval and a push: what the approval card draws and what the publish
/// rule judges. An engineering run projects its selected candidate into one (<see cref="EngineerRun.ToSelectedChange"/>), and
/// it is the stored state of a retired <c>flow.change</c> run that is still in flight. Nothing starts one from a payload.
/// Contains zero public setters and enforces valid stage transitions and human-only publish invariants.
/// </summary>
internal sealed record Change
{
    public Change()
    {
    }

    [JsonConstructor]
    internal Change(
        string? runId,
        RepositoryId? repository,
        string? goal,
        BranchName? requestedBaseBranch,
        WorkTreeLease? tree,
        CoderEdit? edit,
        ChangeSet? commit,
        ChangeApproval? approval,
        ChangePublication? publication)
    {
        RunId = runId ?? string.Empty;
        Repository = repository;
        Goal = goal ?? string.Empty;
        RequestedBaseBranch = requestedBaseBranch;
        Tree = tree;
        Edit = edit;
        Commit = commit;
        Approval = approval;
        Publication = publication;
    }

    public string RunId { get; private init; } = string.Empty;

    public RepositoryId? Repository { get; private init; }

    public string Goal { get; private init; } = string.Empty;

    public BranchName? RequestedBaseBranch { get; private init; }

    public WorkTreeLease? Tree { get; private init; }

    public CoderEdit? Edit { get; private init; }

    public ChangeSet? Commit { get; private init; }

    public ChangeApproval? Approval { get; private init; }

    public ChangePublication? Publication { get; private init; }

    [JsonIgnore]
    public bool IsInitialized => Repository is not null && !string.IsNullOrWhiteSpace(Goal);

    public static Change Start(
        string? runId,
        RepositoryId repository,
        string goal,
        BranchName? requestedBaseBranch = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);

        return new Change
        {
            RunId = runId?.Trim() ?? string.Empty,
            Repository = repository,
            Goal = goal.Trim(),
            RequestedBaseBranch = requestedBaseBranch,
        };
    }

    public Change WithWorkTree(string runId, WorkTreeLease lease)
    {
        if (!IsInitialized)
        {
            throw new InvalidOperationException("Cannot attach a worktree before Change has a Repository and Goal.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(lease);

        return this with
        {
            RunId = runId.Trim(),
            Tree = lease,
        };
    }

    public Change WithCoderEdit(CoderEditResult result)
    {
        if (Tree is null)
        {
            throw new InvalidOperationException("Cannot record coder edits without an active worktree.");
        }

        ArgumentNullException.ThrowIfNull(result);

        var touched = result.TouchedFiles
            .Select(static p => RepoPath.TryParse(p, out var parsed) ? parsed : null)
            .OfType<RepoPath>()
            .Distinct()
            .OrderBy(static p => p.Value, StringComparer.Ordinal)
            .ToArray();

        return this with
        {
            Edit = new CoderEdit(result.Summary ?? string.Empty, result.ModelId ?? string.Empty, touched),
        };
    }

    public Change WithCommit(ChangeSet commit)
    {
        if (Tree is null)
        {
            throw new InvalidOperationException("Cannot record a commit without an active worktree.");
        }

        ArgumentNullException.ThrowIfNull(commit);
        return this with { Commit = commit };
    }

    public Change WithApprovalPending(string planId, DateTimeOffset expires)
    {
        if (Commit is null)
        {
            throw new InvalidOperationException("Cannot enter approval-gate without a verified commit.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        return this with { Approval = new ChangeApproval(planId, expires) };
    }

    /// <summary>
    /// Enforces the domain publish rule: a commit cannot be published before it is explicitly approved
    /// by a human Person for this exact verified commit SHA.
    /// </summary>
    public string EnsureCanPublish(ConfirmationResult? outcome, CommitSha headSha)
    {
        ArgumentNullException.ThrowIfNull(headSha);

        if (Tree is null)
        {
            throw new SecurityException("Refusing to publish: worktree lease is missing.");
        }

        if (Commit is null)
        {
            throw new SecurityException("Refusing to publish: verified commit is missing.");
        }

        if (Approval is null || string.IsNullOrWhiteSpace(Approval.PlanId))
        {
            throw new SecurityException("Refusing to publish: no approval plan was raised for this change.");
        }

        if (headSha != Commit.Commit)
        {
            throw new SecurityException(
                $"Refusing to publish: commit SHA '{headSha.Value}' does not match verified commit '{Commit.Commit.Value}'.");
        }

        if (outcome is null)
        {
            throw new SecurityException($"Refusing to publish: no authoritative confirmation outcome exists for '{Approval.PlanId}'.");
        }

        if (outcome.Outcome != ConfirmationOutcome.Approved || outcome.Skipped)
        {
            throw new SecurityException(
                $"Refusing to publish: confirmation '{Approval.PlanId}' is not explicitly human-approved (Outcome={outcome.Outcome}, Skipped={outcome.Skipped}).");
        }

        if (outcome.Approver is not { IsHuman: true } humanApprover)
        {
            throw new SecurityException(
                $"Refusing to publish: confirmation '{Approval.PlanId}' approver '{outcome.Approver?.Id ?? "none"}' ({outcome.Approver?.Kind}) is not a human Person.");
        }

        return humanApprover.Id;
    }

    public Change WithPushed(string approvedBy, CommitSha pushedSha, string pushedRef)
    {
        if (Commit is null || Approval is null)
        {
            throw new InvalidOperationException("Cannot record push checkpoint before commit and approval.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);
        ArgumentNullException.ThrowIfNull(pushedSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(pushedRef);

        if (pushedSha != Commit.Commit)
        {
            throw new SecurityException(
                $"Refusing to record push for '{pushedSha.Value}'; expected verified commit '{Commit.Commit.Value}'.");
        }

        return this with
        {
            Approval = Approval with { ApprovedBy = approvedBy },
            Publication = (Publication ?? new ChangePublication()) with { PushedRef = pushedRef },
        };
    }

    public Change WithPullRequest(string? pullRequestUrl)
    {
        if (Publication is null || string.IsNullOrWhiteSpace(Publication.PushedRef))
        {
            throw new InvalidOperationException("Cannot record PR checkpoint before push has completed.");
        }

        return this with
        {
            Publication = Publication with
            {
                PullRequestUrl = string.IsNullOrWhiteSpace(pullRequestUrl) ? null : pullRequestUrl.Trim(),
                PullRequestCompleted = true,
            },
        };
    }

    public Change WithCleaned()
    {
        if (Publication is null || string.IsNullOrWhiteSpace(Publication.PushedRef))
        {
            throw new InvalidOperationException("Cannot record cleanup checkpoint before push has completed.");
        }

        return this with
        {
            Publication = Publication with { Cleaned = true },
        };
    }
}
