using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>
/// Single-instance Feature-to-PR durable flow (<c>flow.change</c>) preserved as a thin wire-compatible adapter
/// over <see cref="EngineerPipelineStages"/> (<c>prepare-worktree</c> -&gt; <c>edit</c> -&gt; <c>verify-and-commit</c>
/// -&gt; <c>approval-gate</c> -&gt; <c>push</c> -&gt; <c>pr</c> -&gt; <c>clean</c>).
/// </summary>
[DigitalBrain.Neurons.Neuron(Alias)]
internal sealed class FeatureToPrFlow(
    IWorkTree? workTree = null,
    ICoder? coder = null,
    IConfirmationRequests? confirmationRequests = null,
    IServiceProvider? services = null) : Flow<Change>, IFlowStarter
{
    public const string Alias = IFlows.ChangeFlow;
    public const string ConfirmationKind = "f2pr.publish";
    private const string PlanPrefix = "app:change";

    public static readonly TimeSpan ApprovalWindow = EngineerPipelineStages.DefaultApprovalWindow;

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

    internal static readonly JsonSerializerOptions Json = EngineerPipelineStages.Json;

    protected override IReadOnlyList<string> Steps => Names;

    private IWorkTree WorkTree => workTree
        ?? services?.GetService(typeof(IWorkTree)) as IWorkTree
        ?? throw new InvalidOperationException("IWorkTree service is not registered.");

    private ICoder Coder => coder
        ?? services?.GetService(typeof(ICoder)) as ICoder
        ?? throw new InvalidOperationException("ICoder service is not registered.");

    private IConfirmationRequests ConfirmationRequests =>
        confirmationRequests
        ?? services?.GetService(typeof(IConfirmationRequests)) as IConfirmationRequests
        ?? Context.Get<IConfirmationRequests>(IConfirmationRequests.Of(Id.Brain));

    public Task StartFromJson(string title, string owner, string? parentFlowId, string innerJson)
        => Start(title, owner, parentFlowId, Change.FromUntrustedJson(innerJson, Json));

    public Task<FlowHead> Head() => Task.FromResult(EngineerPipelineStages.BuildHead(
        Names,
        State.Owner,
        State.TraceId,
        State.ParentFlowId,
        State.Started,
        State.Inner,
        State.Title,
        State.Status,
        State.Done,
        State.Ended));

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
            IReadOnlyList<WorkTreeLease> activeLeases = inner.Tree is { } lease ? [lease] : [];
            await EngineerPipelineStages.CleanupFailedLeasesBestEffortAsync(
                WorkTree,
                activeLeases,
                inner.Publication?.Cleaned == true,
                OsConsoleLog.For<FeatureToPrFlow>()).ConfigureAwait(true);

            if (ex is FlowFailed)
            {
                throw;
            }

            throw new FlowFailed(step, ex.Message);
        }
    }

    protected override ConfirmationPlan PlanFor(string step, Change inner)
    {
        var verifiedCommit = inner.Commit?.Commit
            ?? throw new InvalidOperationException("Cannot raise Feature-to-PR confirmation plan without a verified CommitSha.");
        var planId = BuildPlanId(EffectiveRunId(inner), verifiedCommit.Value);
        return EngineerPipelineStages.BuildConfirmationPlan(
            planId,
            ConfirmationKind,
            "Discard Worktree",
            ApprovalWindow,
            inner,
            verifiedCommit);
    }

    public static string BuildPlanId(string runId, string commitSha)
        => EngineerPipelineStages.BuildPlanId(PlanPrefix, runId, commitSha);

    private async Task<StepResult<Change>> PrepareWorktreeStepAsync(Change inner, CancellationToken ct)
    {
        var runId = EffectiveRunId(inner);
        IReadOnlyList<WorkTreeLease> existing = inner.Tree is { } lease ? [lease] : [];
        var (leases, subject) = await EngineerPipelineStages.PrepareWorktreesAsync(
            WorkTree,
            inner.Repository,
            inner.Goal,
            runId,
            instanceCount: 1,
            inner.RequestedBaseBranch,
            existing,
            Id.ToString(),
            "prepare-worktree",
            ct).ConfigureAwait(true);

        return inner.WithWorkTree(runId, leases[0]).Advance(subject);
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
        var committed = await EngineerPipelineStages.VerifyCommitAndCheckCardAsync(
            WorkTree,
            inner.Tree,
            inner.Goal,
            inner.Edit?.Summary,
            inner.WithCommit,
            "verify-and-commit",
            ct).ConfigureAwait(true);

        return inner.WithCommit(committed).Advance();
    }

    private StepResult<Change> ApprovalGateStep(string step, Change inner)
    {
        var (planId, expires) = EngineerPipelineStages.PrepareApprovalGate(
            inner,
            inner.Commit,
            PlanPrefix,
            EffectiveRunId(inner),
            Context.Time.GetUtcNow(),
            ApprovalWindow,
            step);

        return inner.WithApprovalPending(planId, expires).WaitApproval(step, expires);
    }

    private async Task<StepResult<Change>> PushStepAsync(Change inner, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(inner.Publication?.PushedRef))
        {
            return inner.Advance();
        }

        var commit = inner.Commit ?? throw new FlowFailed("push", "Verified CommitSha is missing.");
        var planId = inner.Approval?.PlanId ?? BuildPlanId(EffectiveRunId(inner), commit.Commit.Value);
        var (approvedBy, pushedCommit, pushedRef) = await EngineerPipelineStages.ExecutePushAsync(
            WorkTree,
            ConfirmationRequests,
            inner.Tree,
            commit,
            planId,
            inner.EnsureCanPublish,
            ct).ConfigureAwait(true);

        return inner.WithPushed(approvedBy, pushedCommit, pushedRef).Advance();
    }

    private async Task<StepResult<Change>> PullRequestStepAsync(Change inner, CancellationToken ct)
    {
        if (inner.Publication?.PullRequestCompleted == true)
        {
            return inner.Advance();
        }

        var prUrl = await EngineerPipelineStages.ExecutePullRequestAsync(
            WorkTree,
            inner.Tree,
            inner.Commit,
            inner.Approval?.ApprovedBy,
            inner.Approval?.PlanId,
            inner.Goal,
            inner.Edit?.Summary,
            inner.Edit?.ModelId,
            engineerContext: null,
            ct).ConfigureAwait(true);

        return inner.WithPullRequest(prUrl).Advance();
    }

    private async Task<StepResult<Change>> CleanStepAsync(Change inner, CancellationToken ct)
    {
        if (inner.Publication?.Cleaned == true)
        {
            return inner.Advance();
        }

        IReadOnlyList<WorkTreeLease> active = inner.Tree is { } lease ? [lease] : [];
        await EngineerPipelineStages.ExecuteCleanupAsync(WorkTree, active, ct).ConfigureAwait(true);
        return inner.WithCleaned().Advance();
    }

    private string EffectiveRunId(Change inner)
        => EngineerPipelineStages.ResolveEffectiveRunId(inner.RunId, () => Id.Key);
}
