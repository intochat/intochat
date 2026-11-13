using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Work;

[NeuronAlias(WorkOrchestratorNeuron.NeuronType), DefaultNeuronType(WorkOrchestratorNeuron.NeuronType)]
public interface IWorkOrchestrator
{
    static DigitalBrain.NeuronId Key(string brain) => new(new DigitalBrain.BrainId(brain), WorkOrchestratorNeuron.NeuronType, "default");

    Task<WorkRequest> InitiateWork(string goal, string subject, WorkKind kind = WorkKind.Change, CancellationToken ct = default);

    Task<WorkStageTransition> TransitionStage(string workId, WorkStage current, WorkStage next, WorkStageOutcome outcome, string? detail = null);

    Task<ApprovalResolution> ResolveApproval(string planId, ApprovalStatus status, string resolvedBy, string? reason = null);

    Task<ChangeRun> RegisterChangeRun(ChangeRun run, CancellationToken ct = default);

    Task<ChangeRun?> GetChangeRun(string runId, CancellationToken ct = default);

    Task<IReadOnlyList<ChangeRun>> ActiveChangeRuns(CancellationToken ct = default);

    Task<ChangeStageTransition> TransitionChangeStage(string runId, ChangeStage current, ChangeStage next, WorkStageOutcome outcome, string? detail = null);
}

