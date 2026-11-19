using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain.Confirmation;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;

namespace IntoChat.OS.Modules.Apps.Flows;

internal sealed record EngineerInstance(
    int InstanceIndex,
    WorkTreeLease? Lease,
    string[] CompletedTaskIds,
    CoderEdit? Edit = null,
    WorktreeDiagnostic[]? RoslynDiagnostics = null,
    ChangeSet? Commit = null,
    EngineerBuildReport? Build = null,
    EngineerLiveReport? Live = null,
    string Status = "pending",
    string? FailureDetail = null);

/// <summary>
/// Immutable IntoChat-Engineer pipeline aggregate (<c>flow.engineer</c>) orchestrating 1..N isolated worktree instances
/// across planning, Roslyn/Dart implementation with per-task checkpoints, build/test, live behavioral verification,
/// and SHA-bound human PR publication.
/// </summary>
internal sealed record EngineerRun
{
    public const int MaxParallelInstances = 8;

    public EngineerRun()
    {
    }

    [JsonConstructor]
    internal EngineerRun(
        string? runId,
        RepositoryId? repository,
        string? goal,
        BranchName? requestedBaseBranch,
        int instanceCount,
        EngineerTaskPlan? plan,
        EngineerInstance[]? instances,
        int selectedInstanceIndex,
        ChangeApproval? approval,
        ChangePublication? publication)
    {
        RunId = runId ?? string.Empty;
        Repository = repository;
        Goal = goal ?? string.Empty;
        RequestedBaseBranch = requestedBaseBranch;
        InstanceCount = Math.Clamp(instanceCount <= 0 ? 1 : instanceCount, 1, MaxParallelInstances);
        Plan = plan;
        Instances = instances ?? [];
        SelectedInstanceIndex = Math.Max(0, selectedInstanceIndex);
        Approval = approval;
        Publication = publication;
    }

    public string RunId { get; private init; } = string.Empty;

    public RepositoryId? Repository { get; private init; }

    public string Goal { get; private init; } = string.Empty;

    public BranchName? RequestedBaseBranch { get; private init; }

    public int InstanceCount { get; private init; } = 1;

    public EngineerTaskPlan? Plan { get; private init; }

    public EngineerInstance[] Instances { get; private init; } = [];

    public int SelectedInstanceIndex { get; private init; }

    public ChangeApproval? Approval { get; private init; }

    public ChangePublication? Publication { get; private init; }

    [JsonIgnore]
    public bool IsInitialized => Repository is not null && !string.IsNullOrWhiteSpace(Goal);

    [JsonIgnore]
    public EngineerInstance? SelectedInstance
        => Instances.FirstOrDefault(i => i.InstanceIndex == SelectedInstanceIndex)
            ?? Instances.FirstOrDefault();

    public static EngineerRun Start(
        string? runId,
        RepositoryId repository,
        string goal,
        BranchName? requestedBaseBranch = null,
        int instanceCount = 1)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(instanceCount, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(instanceCount, MaxParallelInstances);

        return new EngineerRun
        {
            RunId = runId?.Trim() ?? string.Empty,
            Repository = repository,
            Goal = goal.Trim(),
            RequestedBaseBranch = requestedBaseBranch,
            InstanceCount = instanceCount,
        };
    }

    public static EngineerRun FromUntrustedJson(string innerJson, JsonSerializerOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(innerJson);
        ArgumentNullException.ThrowIfNull(options);

        var untrusted = JsonSerializer.Deserialize<StartPayload>(innerJson, options)
            ?? throw new ArgumentException("Invalid Engineer start payload.", nameof(innerJson));

        var repository = RepositoryId.Parse(untrusted.Repository ?? string.Empty);
        var requestedBase = string.IsNullOrWhiteSpace(untrusted.RequestedBaseBranch)
            ? null
            : BranchName.FromRequestedBase(untrusted.RequestedBaseBranch);
        var count = untrusted.InstanceCount is null or <= 0 ? 1 : untrusted.InstanceCount.Value;

        return Start(untrusted.RunId, repository, untrusted.Goal ?? string.Empty, requestedBase, count);
    }

    public EngineerRun WithPlan(EngineerTaskPlan plan)
    {
        if (!IsInitialized)
        {
            throw new InvalidOperationException("Cannot attach a task plan before EngineerRun has a Repository and Goal.");
        }

        ArgumentNullException.ThrowIfNull(plan);
        return this with { Plan = plan };
    }

    public EngineerRun WithLeases(string runId, IReadOnlyList<WorkTreeLease> leases)
    {
        if (Plan is null)
        {
            throw new InvalidOperationException("Cannot attach worktree leases before brainstorm-and-plan completes.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(leases);
        if (leases.Count == 0)
        {
            throw new ArgumentException("At least one WorkTreeLease is required.", nameof(leases));
        }

        var updated = new EngineerInstance[leases.Count];
        for (var i = 0; i < leases.Count; i++)
        {
            var lease = leases[i];
            var idx = lease.InstanceIndex;
            var prior = Instances.FirstOrDefault(inst => inst.InstanceIndex == idx);
            updated[i] = prior is not null
                ? prior with { Lease = lease, Status = prior.Commit is not null ? prior.Status : "prepared" }
                : new EngineerInstance(idx, lease, [], Status: "prepared");
        }

        return this with
        {
            RunId = runId.Trim(),
            Instances = updated,
        };
    }

    public EngineerRun WithUpdatedInstances(IReadOnlyList<EngineerInstance> instances, int? selectedIndex = null)
    {
        ArgumentNullException.ThrowIfNull(instances);
        return this with
        {
            Instances = [.. instances.OrderBy(static i => i.InstanceIndex)],
            SelectedInstanceIndex = selectedIndex ?? SelectedInstanceIndex,
        };
    }

    public EngineerRun WithApprovalPending(string planId, DateTimeOffset expires)
    {
        var winner = SelectedInstance
            ?? throw new InvalidOperationException("Cannot enter approval-gate without a verified worktree instance.");
        if (winner.Commit is null || winner.Build is not { Succeeded: true } || winner.Live is not { Succeeded: true })
        {
            throw new InvalidOperationException("Cannot enter approval-gate before commit, build-and-test, and live-verify succeed.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        return this with { Approval = new ChangeApproval(planId, expires) };
    }

    public Change ToSelectedChange(ChangeSet? overrideCommit = null)
    {
        if (Repository is null)
        {
            return new Change();
        }

        var winner = SelectedInstance;
        var change = Change.Start(RunId, Repository, Goal, RequestedBaseBranch);
        if (winner?.Lease is { } lease)
        {
            change = change.WithWorkTree(string.IsNullOrWhiteSpace(RunId) ? lease.RunId : RunId, lease);
            if (winner.Edit is { } edit)
            {
                change = change.WithCoderEdit(new CoderEditResult(
                    edit.Summary,
                    [.. edit.TouchedFiles.Select(static f => f.Value)],
                    edit.ModelId));
            }

            if ((overrideCommit ?? winner.Commit) is { } commit)
            {
                change = change.WithCommit(commit);
            }
        }

        return change;
    }

    public string EnsureCanPublish(ConfirmationResult? outcome, CommitSha headSha)
    {
        ArgumentNullException.ThrowIfNull(headSha);
        var winner = SelectedInstance
            ?? throw new SecurityException("Refusing to publish: no verified candidate instance exists.");

        if (winner.Build is not { Succeeded: true })
        {
            throw new SecurityException("Refusing to publish: build-and-test verification has not succeeded.");
        }

        if (winner.Live is not { Succeeded: true })
        {
            throw new SecurityException("Refusing to publish: live behavioral verification has not succeeded.");
        }

        var change = ToSelectedChange();
        if (Approval is { } approval)
        {
            change = change.WithApprovalPending(approval.PlanId, approval.Expires);
        }

        return change.EnsureCanPublish(outcome, headSha);
    }

    public EngineerRun WithPushed(string approvedBy, CommitSha pushedSha, string pushedRef)
    {
        var winner = SelectedInstance
            ?? throw new InvalidOperationException("Cannot record push without a selected instance.");
        if (winner.Commit is null || Approval is null)
        {
            throw new InvalidOperationException("Cannot record push before commit and approval.");
        }

        if (pushedSha != winner.Commit.Commit)
        {
            throw new SecurityException(
                $"Refusing to record push for '{pushedSha.Value}'; expected verified commit '{winner.Commit.Commit.Value}'.");
        }

        return this with
        {
            Approval = Approval with { ApprovedBy = approvedBy },
            Publication = (Publication ?? new ChangePublication()) with { PushedRef = pushedRef },
        };
    }

    public EngineerRun WithPullRequest(string? pullRequestUrl)
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

    public EngineerRun WithCleaned()
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

    private sealed record StartPayload(
        string? RunId,
        string? Repository,
        string? Goal,
        string? RequestedBaseBranch,
        int? InstanceCount);
}
