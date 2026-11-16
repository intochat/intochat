using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Log;
using IntoChat.OS.Framework.Logging;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Primitives;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>
/// Hardened Feature-to-PR durable flow (<c>flow.change</c>) using immutable <see cref="Change"/> transitions
/// and checkpointed idempotent publish stages (<c>push</c> -&gt; <c>pr</c> -&gt; <c>clean</c>).
/// </summary>
[DigitalBrain.Neurons.Neuron(Alias)]
public sealed class FeatureToPrFlow(
    IWorkTree? workTree = null,
    ICoder? coder = null,
    IConfirmationRequests? confirmationRequests = null,
    IServiceProvider? services = null) : Flow<Change>, IFlowStarter
{
    public const string Alias = "flow.change";
    public const string ConfirmationKind = "f2pr.publish";

    public static readonly TimeSpan ApprovalWindow = TimeSpan.FromHours(48);

    public static readonly string[] Names =
    [
        "prepare-worktree",
        "edit",
        "verify-and-commit",
        "approval-gate",
        "push",
        "pr",
        "clean",
    ];

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override IReadOnlyList<string> Steps => Names;

    private IWorkTree WorkTree => workTree
        ?? services?.GetService(typeof(IWorkTree)) as IWorkTree
        ?? new WorkTreeService();

    private ICoder Coder => coder
        ?? services?.GetService(typeof(ICoder)) as ICoder
        ?? new LocalLlmCoder(services);

    private IConfirmationRequests ConfirmationRequests =>
        confirmationRequests
        ?? services?.GetService(typeof(IConfirmationRequests)) as IConfirmationRequests
        ?? Context.Get<IConfirmationRequests>(IConfirmationRequests.Of(Id.Brain));

    public Task StartFromJson(string title, string owner, string? parentFlowId, string innerJson)
    {
        var sanitized = Change.FromUntrustedJson(innerJson, Json);
        return Start(title, owner, parentFlowId, sanitized);
    }

    public Task<FlowHead> Head() => Task.FromResult(new FlowHead(
        [.. Names],
        State.Owner,
        State.TraceId,
        State.ParentFlowId,
        State.Started,
        JsonSerializer.Serialize(State.Inner, Json),
        State.Title,
        State.Status,
        [.. State.Done],
        State.Ended,
        null));

    protected override async Task<StepResult<Change>> Run(string step, Change inner, CancellationToken ct)
    {
        try
        {
            return step switch
            {
                "prepare-worktree" => await PrepareWorktreeStepAsync(inner, ct).ConfigureAwait(true),
                "edit" => await EditStepAsync(inner, ct).ConfigureAwait(true),
                "verify-and-commit" => await VerifyAndCommitStepAsync(inner, ct).ConfigureAwait(true),
                "approval-gate" => ApprovalGateStep(step, inner),
                "push" => await PushStepAsync(inner, ct).ConfigureAwait(true),
                "pr" => await PullRequestStepAsync(inner, ct).ConfigureAwait(true),
                "clean" => await CleanStepAsync(inner, ct).ConfigureAwait(true),
                _ => throw new FlowFailed(step, $"Unknown FeatureToPrFlow step '{step}'."),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !Context.Interrupts(ex))
        {
            if (inner.Tree is { } lease && inner.Publication?.Cleaned != true)
            {
                try
                {
                    await WorkTree.CleanupAsync(lease, CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception cleanupEx) when (cleanupEx is not OperationCanceledException)
                {
                    OsConsoleLog.For<FeatureToPrFlow>().LogSwallowed(cleanupEx, "Best-effort worktree cleanup after failed step.");
                }
            }

            if (ex is FlowFailed)
            {
                throw;
            }

            throw new FlowFailed(step, ex.Message);
        }
    }

    protected override ConfirmationPlan PlanFor(string step, Change inner)
    {
        var runId = EffectiveRunId(inner);
        var sha = inner.Commit?.Commit.Value
            ?? throw new InvalidOperationException("Cannot raise Feature-to-PR confirmation plan without a verified CommitSha.");
        var planId = BuildPlanId(runId, sha);
        var repoLabel = inner.Tree?.GitHubRepo ?? inner.Tree?.RepositoryId ?? inner.Repository?.Value ?? string.Empty;

        return new ConfirmationPlan(
            Id: planId,
            Action: ConfirmationAction.Other,
            Destructive: true,
            Title: $"Push & open Draft PR on {repoLabel} ({sha[..Math.Min(8, sha.Length)]})?",
            Body: ChangeCard.Body(inner),
            ApproveLabel: "Push & Open Draft PR",
            DenyLabel: "Discard Worktree",
            TargetLabel: $"{repoLabel}@{sha[..Math.Min(8, sha.Length)]}")
        {
            RequiresPerson = true,
            Expiry = ApprovalWindow,
            Kind = ConfirmationKind,
            Subject = sha,
        };
    }

    public static string BuildPlanId(string runId, string commitSha)
    {
        var cleanRun = string.IsNullOrWhiteSpace(runId) ? "run" : runId.Trim();
        if (cleanRun.Length > 48)
        {
            cleanRun = cleanRun[..48];
        }

        return $"app:change:{cleanRun}:{commitSha.Trim()}";
    }

    private async Task<StepResult<Change>> PrepareWorktreeStepAsync(Change inner, CancellationToken ct)
    {
        if (inner.Repository is null)
        {
            throw new FlowFailed("prepare-worktree", "Repository id is required.");
        }

        if (string.IsNullOrWhiteSpace(inner.Goal))
        {
            throw new FlowFailed("prepare-worktree", "Goal description is required.");
        }

        var runId = EffectiveRunId(inner);

        if (inner.Tree is not null && Directory.Exists(inner.Tree.TreePath))
        {
            await WorkTreeService.VerifyTripleGitLockAsync(inner.Tree, ct).ConfigureAwait(true);
            return inner.WithWorkTree(runId, inner.Tree)
                .Advance(new FlowSubject(Id.ToString(), $"{inner.Repository.Value}:{inner.Tree.BranchName}"));
        }

        var wt = WorkTree;
        var lease = await Task.Run(
            () => wt.PrepareAsync(inner.Repository, runId, inner.Goal, inner.RequestedBaseBranch, ct),
            ct).ConfigureAwait(true);
        return inner.WithWorkTree(runId, lease)
            .Advance(new FlowSubject(Id.ToString(), $"{lease.RepositoryId}:{lease.BranchName}"));
    }

    private async Task<StepResult<Change>> EditStepAsync(Change inner, CancellationToken ct)
    {
        var lease = inner.Tree ?? throw new FlowFailed("edit", "Worktree lease is missing.");
        await WorkTreeService.VerifyTripleGitLockAsync(lease, ct).ConfigureAwait(true);

        if (inner.Commit is not null)
        {
            return inner.Advance();
        }

        var activeCoder = Coder;
        var editResult = await Task.Run(() => activeCoder.EditAsync(lease, inner.Goal, ct), ct).ConfigureAwait(true);
        return inner.WithCoderEdit(editResult).Advance();
    }

    private async Task<StepResult<Change>> VerifyAndCommitStepAsync(Change inner, CancellationToken ct)
    {
        var lease = inner.Tree ?? throw new FlowFailed("verify-and-commit", "Worktree lease is missing.");
        var commitTitle = ChangePolicy.BuildCommitMessage(inner.Goal, inner.Edit?.Summary);
        var wt = WorkTree;
        var committed = await Task.Run(() => wt.VerifyAndCommitAsync(lease, commitTitle, ct), ct).ConfigureAwait(true);

        // Ask before storing the diff on state: a refused diff stays out of persisted state.
        var metadataWithoutDiff = ChangeSet.Create(
            committed.Commit,
            committed.ChangedFiles,
            string.Empty,
            committed.Additions,
            committed.Deletions);
        var preview = inner.WithCommit(metadataWithoutDiff);
        if (ChangeCard.Refusal(preview, committed.HostRenderedDiff) is { } refusal)
        {
            throw new FlowFailed("verify-and-commit", refusal);
        }

        return inner.WithCommit(committed).Advance();
    }

    private StepResult<Change> ApprovalGateStep(string step, Change inner)
    {
        var commit = inner.Commit ?? throw new FlowFailed(step, "Cannot enter approval-gate without a verified CommitSha.");

        if (ChangeCard.Refusal(inner, commit.HostRenderedDiff) is { } refusal)
        {
            throw new FlowFailed(step, refusal);
        }

        var runId = EffectiveRunId(inner);
        var planId = BuildPlanId(runId, commit.Commit.Value);
        var expires = Context.Time.GetUtcNow().Add(ApprovalWindow);

        return inner.WithApprovalPending(planId, expires).WaitApproval(step, expires);
    }

    private async Task<StepResult<Change>> PushStepAsync(Change inner, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(inner.Publication?.PushedRef))
        {
            return inner.Advance();
        }

        var lease = inner.Tree ?? throw new FlowFailed("push", "Worktree lease is missing.");
        var commit = inner.Commit ?? throw new FlowFailed("push", "Verified CommitSha is missing.");
        var planId = inner.Approval?.PlanId ?? BuildPlanId(EffectiveRunId(inner), commit.Commit.Value);

        var outcome = await ConfirmationRequests.Outcome(planId).ConfigureAwait(true);
        var humanApprover = inner.EnsureCanPublish(outcome, commit.Commit);

        var wt = WorkTree;
        var pushedRef = await Task.Run(() => wt.PushAsync(lease, commit.Commit, ct), ct).ConfigureAwait(true);

        return inner.WithPushed(humanApprover, commit.Commit, pushedRef).Advance();
    }

    private async Task<StepResult<Change>> PullRequestStepAsync(Change inner, CancellationToken ct)
    {
        if (inner.Publication?.PullRequestCompleted == true)
        {
            return inner.Advance();
        }

        var lease = inner.Tree ?? throw new FlowFailed("pr", "Worktree lease is missing.");
        var commit = inner.Commit ?? throw new FlowFailed("pr", "Verified CommitSha is missing.");
        var approverId = inner.Approval?.ApprovedBy
            ?? throw new FlowFailed("pr", "Human approver is missing.");

        var prTitle = ChangePolicy.BuildPrTitle(inner.Goal);
        var prBody = BuildPrBody(inner, commit.Commit.Value, approverId);

        var wt = WorkTree;
        var prUrl = await Task.Run(
            () => wt.CreateDraftPullRequestAsync(lease, prTitle, prBody, ct),
            ct).ConfigureAwait(true);

        return inner.WithPullRequest(prUrl).Advance();
    }

    private async Task<StepResult<Change>> CleanStepAsync(Change inner, CancellationToken ct)
    {
        if (inner.Publication?.Cleaned == true)
        {
            return inner.Advance();
        }

        if (inner.Tree is { } lease)
        {
            var wt = WorkTree;
            await Task.Run(() => wt.CleanupAsync(lease, ct), ct).ConfigureAwait(true);
        }

        return inner.WithCleaned().Advance();
    }

    private string EffectiveRunId(Change inner)
    {
        if (!string.IsNullOrWhiteSpace(inner.RunId))
        {
            return inner.RunId.Trim();
        }

        try
        {
            return Id.Key;
        }
        catch (InvalidOperationException)
        {
            return Guid.NewGuid().ToString("N")[..12];
        }
    }

    private static string BuildPrBody(Change inner, string sha, string approverId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Summary");
        var cleanSummary = ChangePolicy.SanitizeCommitText(inner.Edit?.Summary);
        sb.AppendLine(string.IsNullOrWhiteSpace(cleanSummary) ? ChangePolicy.SanitizeCommitText(inner.Goal) : cleanSummary);
        sb.AppendLine();
        sb.AppendLine("## Verification & Provenance");
        sb.AppendLine($"- **Goal:** {ChangePolicy.SanitizeCommitText(inner.Goal)}");
        sb.AppendLine($"- **Commit:** `{sha}`");
        var files = inner.Commit?.ChangedFiles ?? [];
        sb.AppendLine($"- **Files ({files.Length}):** {string.Join(", ", files.Select(static f => $"`{f.Value}`"))}");
        sb.AppendLine($"- **Approved by Person:** `{approverId}` (plan `{inner.Approval?.PlanId}`)");
        if (!string.IsNullOrWhiteSpace(inner.Edit?.ModelId))
        {
            sb.AppendLine($"- **Model:** `{inner.Edit.ModelId}`");
        }

        return sb.ToString().Trim();
    }
}
