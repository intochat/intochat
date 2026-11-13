using DigitalBrain.Brains;
using IntoChat.OS.Primitives;

namespace IntoChat.OS.Modules.Conversations;

public sealed class ConversationAggregate
{
    private readonly List<ConversationTurn> _turns = [];
    private readonly List<ActiveRun> _runHistory = [];
    private readonly List<PendingApproval> _approvals = [];

    public ConversationId Id { get; }
    public string BrainId { get; }
    public string Title { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<ConversationTurn> Turns => _turns;
    public ActiveRun? ActiveRun { get; private set; }
    public IReadOnlyList<ActiveRun> RunHistory => _runHistory;
    public IReadOnlyList<PendingApproval> Approvals => _approvals;

    private ConversationAggregate(
        ConversationId id,
        string brainId,
        string title,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Id = id;
        BrainId = brainId;
        Title = title;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public static ConversationAggregate Create(
        string brainId,
        ConversationId id,
        string? title = null,
        DateTimeOffset? createdAt = null)
    {
        if (string.IsNullOrWhiteSpace(brainId) || !BrainScope.IsValidId(brainId))
        {
            throw new ArgumentException($"Invalid brain id '{brainId}'.", nameof(brainId));
        }

        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("Conversation id cannot be empty.", nameof(id));
        }

        var now = createdAt ?? DateTimeOffset.UtcNow;
        var displayTitle = string.IsNullOrWhiteSpace(title) ? "New Conversation" : title.Trim();
        return new ConversationAggregate(id, brainId, displayTitle, now, now);
    }

    public static ConversationAggregate Restore(ConversationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var aggregate = new ConversationAggregate(
            snapshot.Id,
            snapshot.BrainId,
            snapshot.Title,
            snapshot.CreatedAt,
            snapshot.UpdatedAt);

        aggregate._turns.AddRange(snapshot.Turns);
        aggregate.ActiveRun = snapshot.ActiveRun;
        aggregate._runHistory.AddRange(snapshot.RunHistory);
        aggregate._approvals.AddRange(snapshot.Approvals);
        return aggregate;
    }

    public void SetTitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Title = title.Trim();
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public ActiveRun StartRun(
        string runId,
        string? flowId = null,
        string? step = null,
        string? prompt = null,
        DateTimeOffset? startedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (ActiveRun is { IsActive: true })
        {
            throw new InvalidOperationException($"Cannot start run '{runId}' because run '{ActiveRun.RunId}' is already active.");
        }

        var now = startedAt ?? DateTimeOffset.UtcNow;
        var run = new ActiveRun(
            RunId: runId.Trim(),
            Status: ConversationRunStatus.Running,
            StartedAt: now,
            FlowId: flowId?.Trim(),
            Step: step?.Trim(),
            Prompt: prompt?.Trim());

        ActiveRun = run;
        UpdatedAt = now;

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            RecordTurn("user", prompt.Trim(), ConversationTurnRole.User, runId.Trim(), null, now);
        }

        return run;
    }

    public void AdvanceRunStep(string runId, string step)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(step);

        if (ActiveRun is null || !string.Equals(ActiveRun.RunId, runId.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Run '{runId}' is not the currently active run.");
        }

        if (!ActiveRun.IsActive)
        {
            throw new InvalidOperationException($"Run '{runId}' is in state '{ActiveRun.Status}' and cannot advance step.");
        }

        ActiveRun = ActiveRun with { Step = step.Trim() };
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void CompleteRun(string runId, string? reply = null, DateTimeOffset? endedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        if (ActiveRun is null || !string.Equals(ActiveRun.RunId, runId.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Run '{runId}' is not the currently active run.");
        }

        var now = endedAt ?? DateTimeOffset.UtcNow;
        var completed = ActiveRun with
        {
            Status = ConversationRunStatus.Completed,
            Reply = reply?.Trim(),
            EndedAt = now,
        };

        _runHistory.Add(completed);
        ActiveRun = null;
        UpdatedAt = now;

        if (!string.IsNullOrWhiteSpace(reply))
        {
            RecordTurn("assistant", reply.Trim(), ConversationTurnRole.Assistant, runId.Trim(), null, now);
        }
    }

    public void FailRun(string runId, string error, DateTimeOffset? endedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        if (ActiveRun is null || !string.Equals(ActiveRun.RunId, runId.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Run '{runId}' is not the currently active run.");
        }

        var now = endedAt ?? DateTimeOffset.UtcNow;
        var failed = ActiveRun with
        {
            Status = ConversationRunStatus.Failed,
            Error = error.Trim(),
            EndedAt = now,
        };

        _runHistory.Add(failed);
        ActiveRun = null;
        UpdatedAt = now;
    }

    public void CancelRun(string runId, DateTimeOffset? endedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        if (ActiveRun is null || !string.Equals(ActiveRun.RunId, runId.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Run '{runId}' is not the currently active run.");
        }

        var now = endedAt ?? DateTimeOffset.UtcNow;
        var cancelled = ActiveRun with
        {
            Status = ConversationRunStatus.Cancelled,
            EndedAt = now,
        };

        _runHistory.Add(cancelled);
        ActiveRun = null;
        UpdatedAt = now;
    }

    public ConversationTurn RecordTurn(
        string speaker,
        string content,
        ConversationTurnRole role = ConversationTurnRole.User,
        string? runId = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        DateTimeOffset? timestamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speaker);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        var now = timestamp ?? DateTimeOffset.UtcNow;
        var turnId = "t-" + Guid.NewGuid().ToString("N");
        var turn = new ConversationTurn(
            turnId,
            speaker.Trim(),
            role,
            content.Trim(),
            now,
            runId?.Trim(),
            metadata);

        _turns.Add(turn);
        UpdatedAt = now;
        return turn;
    }

    public PendingApproval RequestApproval(
        string approvalId,
        ConversationApprovalKind kind,
        string title,
        string detail,
        string? flowId = null,
        string? step = null,
        string? runId = null,
        DateTimeOffset? requestedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var cleanId = approvalId.Trim();
        if (_approvals.Any(a => string.Equals(a.ApprovalId, cleanId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Approval '{cleanId}' has already been requested.");
        }

        var now = requestedAt ?? DateTimeOffset.UtcNow;
        var associatedRunId = runId?.Trim() ?? ActiveRun?.RunId;
        var associatedFlowId = flowId?.Trim() ?? ActiveRun?.FlowId;
        var associatedStep = step?.Trim() ?? ActiveRun?.Step;

        var approval = new PendingApproval(
            cleanId,
            Id,
            kind,
            title.Trim(),
            detail?.Trim() ?? string.Empty,
            ConversationApprovalStatus.Pending,
            now,
            associatedFlowId,
            associatedStep,
            associatedRunId);

        _approvals.Add(approval);

        if (ActiveRun is not null && (associatedRunId == null || string.Equals(ActiveRun.RunId, associatedRunId, StringComparison.Ordinal)))
        {
            ActiveRun = ActiveRun with { Status = ConversationRunStatus.WaitingApproval };
        }

        UpdatedAt = now;
        return approval;
    }

    public PendingApproval ResolveApproval(
        string approvalId,
        bool approved,
        string? resolver = null,
        string? reason = null,
        DateTimeOffset? resolvedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);

        var cleanId = approvalId.Trim();
        var index = _approvals.FindIndex(a => string.Equals(a.ApprovalId, cleanId, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new InvalidOperationException($"Approval '{cleanId}' was not found.");
        }

        var existing = _approvals[index];
        if (!existing.IsPending)
        {
            throw new InvalidOperationException($"Approval '{cleanId}' is already resolved with status '{existing.Status}'.");
        }

        var now = resolvedAt ?? DateTimeOffset.UtcNow;
        var status = approved ? ConversationApprovalStatus.Approved : ConversationApprovalStatus.Denied;
        var resolved = existing with
        {
            Status = status,
            ResolvedAt = now,
            ResolvedBy = resolver?.Trim(),
            ResolutionReason = reason?.Trim(),
        };

        _approvals[index] = resolved;

        if (ActiveRun is not null && string.Equals(ActiveRun.RunId, existing.RunId, StringComparison.Ordinal))
        {
            if (approved)
            {
                if (ActiveRun.Status == ConversationRunStatus.WaitingApproval)
                {
                    ActiveRun = ActiveRun with { Status = ConversationRunStatus.Running };
                }
            }
            else
            {
                var failureMessage = $"Approval '{cleanId}' denied: {reason?.Trim() ?? "User denied approval"}";
                var failed = ActiveRun with
                {
                    Status = ConversationRunStatus.Failed,
                    Error = failureMessage,
                    EndedAt = now,
                };
                _runHistory.Add(failed);
                ActiveRun = null;
            }
        }

        UpdatedAt = now;
        return resolved;
    }

    public ConversationSnapshot ToSnapshot()
        => new(
            Id,
            BrainId,
            Title,
            CreatedAt,
            UpdatedAt,
            [.. _turns],
            ActiveRun,
            [.. _runHistory],
            [.. _approvals]);
}
