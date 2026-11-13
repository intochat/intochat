using DigitalBrain.Brains;
using DigitalBrain.Neurons;
using IntoChat.OS.Primitives;

namespace IntoChat.OS.Modules.Conversations;

public sealed record ConversationRecord(ConversationSnapshot? Snapshot)
{
    public ConversationRecord() : this((ConversationSnapshot?)null) { }
}

[Neuron("conversations")]
public sealed class ConversationNeuron(IConversationDomainService domainService)
    : DigitalBrain.Neurons.Neuron<ConversationRecord>, IConversationNeuron
{
    public ConversationNeuron() : this(new ConversationDomainService()) { }

    private string CurrentBrainId
    {
        get
        {
            var brain = Id.Brain.Value;
            domainService.ValidateBrainId(brain);
            return brain;
        }
    }

    private ConversationId CurrentConversationId
    {
        get
        {
            var key = Id.Key;
            const string Prefix = "conversations/";
            var raw = key.StartsWith(Prefix, StringComparison.Ordinal) ? key[Prefix.Length..] : key;
            return ConversationId.TryParse(raw, out var parsed) ? parsed : ConversationId.Parse(key);
        }
    }

    private ConversationAggregate EnsureAggregate()
    {
        if (State.Snapshot is { } snapshot)
        {
            return domainService.RestoreConversation(snapshot);
        }

        return domainService.CreateConversation(CurrentBrainId, existingId: CurrentConversationId);
    }

    private async Task PersistAsync(ConversationAggregate aggregate, DigitalBrain.Signal signal)
    {
        var snapshot = aggregate.ToSnapshot();
        await Save(new ConversationRecord(snapshot), signal);
    }

    public Task<ConversationSnapshot> Read()
    {
        var aggregate = EnsureAggregate();
        return Task.FromResult(aggregate.ToSnapshot());
    }

    public async Task<ConversationTurn> AddTurn(string speaker, string content, ConversationTurnRole role = ConversationTurnRole.User, string? runId = null)
    {
        var aggregate = EnsureAggregate();
        var turn = aggregate.RecordTurn(speaker, content, role, runId);
        await PersistAsync(aggregate, new ConversationTurnRecorded(aggregate.Id, turn.TurnId, turn.Speaker));
        return turn;
    }

    public async Task<ActiveRun> StartRun(string? flowId = null, string? step = null, string? prompt = null)
    {
        var aggregate = EnsureAggregate();
        var runId = "run-" + Guid.NewGuid().ToString("N");
        var run = aggregate.StartRun(runId, flowId, step, prompt);
        await PersistAsync(aggregate, new ConversationRunStarted(aggregate.Id, run.RunId));
        return run;
    }

    public async Task AdvanceRunStep(string runId, string step)
    {
        var aggregate = EnsureAggregate();
        aggregate.AdvanceRunStep(runId, step);
        await PersistAsync(aggregate, new ConversationRunStepAdvanced(aggregate.Id, runId, step));
    }

    public async Task CompleteRun(string runId, string? reply = null)
    {
        var aggregate = EnsureAggregate();
        aggregate.CompleteRun(runId, reply);
        await PersistAsync(aggregate, new ConversationRunCompleted(aggregate.Id, runId, ConversationRunStatus.Completed));
    }

    public async Task FailRun(string runId, string error)
    {
        var aggregate = EnsureAggregate();
        aggregate.FailRun(runId, error);
        await PersistAsync(aggregate, new ConversationRunCompleted(aggregate.Id, runId, ConversationRunStatus.Failed));
    }

    public async Task CancelRun(string runId)
    {
        var aggregate = EnsureAggregate();
        aggregate.CancelRun(runId);
        await PersistAsync(aggregate, new ConversationRunCompleted(aggregate.Id, runId, ConversationRunStatus.Cancelled));
    }

    public async Task<PendingApproval> RequestApproval(string approvalId, ConversationApprovalKind kind, string title, string detail, string? flowId = null, string? step = null)
    {
        var aggregate = EnsureAggregate();
        var approval = aggregate.RequestApproval(approvalId, kind, title, detail, flowId, step);
        await PersistAsync(aggregate, new ConversationApprovalChanged(aggregate.Id, approval.ApprovalId, approval.Status));
        return approval;
    }

    public async Task<PendingApproval> ResolveApproval(string approvalId, bool approved, string? resolver = null, string? reason = null)
    {
        var aggregate = EnsureAggregate();
        var approval = aggregate.ResolveApproval(approvalId, approved, resolver, reason);
        await PersistAsync(aggregate, new ConversationApprovalChanged(aggregate.Id, approval.ApprovalId, approval.Status));
        return approval;
    }
}
