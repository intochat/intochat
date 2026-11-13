using System.Collections.Concurrent;
using DigitalBrain.Neurons;

namespace IntoChat.OS.Modules.Work;

/// <summary>
/// Stateless coordinator neuron for orchestrating durable work flows and tracking stage transitions and approvals.
/// </summary>
[Neuron(NeuronType), StatelessNeuron]
public sealed class WorkOrchestratorNeuron(WorkApprovalTracker? tracker = null) : Neuron, IWorkOrchestrator
{
    public const string NeuronType = "work.orchestrator";

    private readonly WorkApprovalTracker _tracker = tracker ?? new WorkApprovalTracker();
    private readonly ConcurrentDictionary<string, ChangeRun> _changeRuns = new(StringComparer.Ordinal);

    public Task<WorkRequest> InitiateWork(string goal, string subject, WorkKind kind = WorkKind.Change, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        var request = new WorkRequest(
            WorkId: Guid.NewGuid().ToString("N"),
            Goal: goal.Trim(),
            Subject: subject.Trim(),
            Kind: kind,
            RequestedBy: Context.Caller?.For?.Principal ?? "system");

        request.Validate();
        return Task.FromResult(request);
    }

    public Task<WorkStageTransition> TransitionStage(string workId, WorkStage current, WorkStage next, WorkStageOutcome outcome, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workId);

        var transition = new WorkStageTransition(
            WorkId: workId,
            FromStage: current,
            ToStage: next,
            Outcome: outcome,
            TransitionedAt: Context.Time.GetUtcNow(),
            Detail: detail);

        return Task.FromResult(transition);
    }

    public Task<ApprovalResolution> ResolveApproval(string planId, ApprovalStatus status, string resolvedBy, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedBy);

        _tracker.TryResolve(planId, status, resolvedBy, Context.Time.GetUtcNow(), reason, out var resolution);

        foreach (var (runId, run) in _changeRuns)
        {
            if (run.ConfirmationPlanId == planId)
            {
                var updated = run with
                {
                    IsApproved = status == ApprovalStatus.Approved,
                    Aborted = status == ApprovalStatus.Denied,
                    AbortReason = reason,
                    Stage = status == ApprovalStatus.Approved ? ChangeStage.Promote : ChangeStage.ApprovalGate,
                    Outcome = status == ApprovalStatus.Approved ? WorkStageOutcome.Succeeded : WorkStageOutcome.Failed
                };
                _changeRuns[runId] = updated;
            }
        }

        return Task.FromResult(resolution);
    }

    public Task<ChangeRun> RegisterChangeRun(ChangeRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        var entry = run.CreatedAt is null ? run with { CreatedAt = Context.Time.GetUtcNow() } : run;
        _changeRuns[entry.RunId] = entry;
        return Task.FromResult(entry);
    }

    public Task<ChangeRun?> GetChangeRun(string runId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        _changeRuns.TryGetValue(runId, out var run);
        return Task.FromResult(run);
    }

    public Task<IReadOnlyList<ChangeRun>> ActiveChangeRuns(CancellationToken ct = default)
    {
        IReadOnlyList<ChangeRun> runs = _changeRuns.Values.OrderByDescending(r => r.CreatedAt).ToArray();
        return Task.FromResult(runs);
    }

    public Task<ChangeStageTransition> TransitionChangeStage(string runId, ChangeStage current, ChangeStage next, WorkStageOutcome outcome, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        if (_changeRuns.TryGetValue(runId, out var run))
        {
            _changeRuns[runId] = run with
            {
                Stage = next,
                Outcome = outcome
            };
        }

        var transition = new ChangeStageTransition(
            RunId: runId,
            FromStage: current,
            ToStage: next,
            Outcome: outcome,
            TransitionedAt: Context.Time.GetUtcNow(),
            Detail: detail);

        return Task.FromResult(transition);
    }
}

