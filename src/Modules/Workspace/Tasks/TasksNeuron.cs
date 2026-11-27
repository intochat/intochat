using System.Globalization;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Confirmation;
using DigitalBrain.Neurons;

namespace IntoChat.OS.Modules.Apps.Tasks;

/// <summary>
/// Durable Orleans neuron backing <see cref="ITasks"/>. Stores workspace tasks without arbitrary item caps,
/// emits reactive <see cref="TaskChangedSignal"/> / <see cref="TaskAskedSignal"/> signals, and enforces
/// person-only authority on <see cref="AnswerAsk"/>.
/// </summary>
[Neuron("tasks")]
internal sealed class TasksNeuron : Neuron<TasksNeuronState>, ITasks
{
    public Task<TaskRecord[]> List(TaskStatus? status = null, string? owner = null, CancellationToken ct = default)
    {
        IEnumerable<TaskRecord> query = State.Tasks;
        if (status is { } s)
        {
            query = query.Where(t => t.Status == s);
        }
        if (!string.IsNullOrWhiteSpace(owner))
        {
            var trimmed = owner.Trim();
            query = query.Where(t => string.Equals(t.Owner, trimmed, StringComparison.Ordinal));
        }

        return Task.FromResult(query.ToArray());
    }

    public Task<TaskRecord?> Get(string taskId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        var trimmed = taskId.Trim();
        return Task.FromResult(State.Tasks.FirstOrDefault(t => string.Equals(t.TaskId, trimmed, StringComparison.Ordinal)));
    }

    public async Task<TaskRecord> Create(
        string title,
        string? description = null,
        string? owner = null,
        string? tag = null,
        string? flowId = null,
        string? conversationId = null,
        string[]? contracts = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var now = Context.Time.GetUtcNow();
        var seq = State.NextSequence;
        var taskId = string.Create(CultureInfo.InvariantCulture, $"task-{seq}");
        while (State.Tasks.Any(t => string.Equals(t.TaskId, taskId, StringComparison.Ordinal)))
        {
            seq++;
            taskId = string.Create(CultureInfo.InvariantCulture, $"task-{seq}");
        }

        var resolvedOwner = string.IsNullOrWhiteSpace(owner)
            ? (CallerRules.ActsFor(Context.Caller) ?? "owner")
            : owner.Trim();
        var resolvedConversation = string.IsNullOrWhiteSpace(conversationId)
            ? Context.Caller?.Conversation
            : conversationId.Trim();

        var record = new TaskRecord(
            TaskId: taskId,
            Title: title.Trim(),
            Description: description?.Trim() ?? string.Empty,
            Status: TaskStatus.Queued,
            Owner: resolvedOwner,
            Tag: string.IsNullOrWhiteSpace(tag) ? "Task" : tag.Trim(),
            CreatedAt: now,
            UpdatedAt: now,
            FlowId: string.IsNullOrWhiteSpace(flowId) ? null : flowId.Trim(),
            ConversationId: string.IsNullOrWhiteSpace(resolvedConversation) ? null : resolvedConversation,
            Contracts: contracts is { Length: > 0 } ? [.. contracts] : []);

        var revision = State.Revision + 1;
        var next = State with
        {
            Tasks = [.. State.Tasks, record],
            Revision = revision,
            NextSequence = seq + 1,
        };
        await Save(next, new TaskChangedSignal(record.TaskId, record.Status, revision)).ConfigureAwait(true);
        return record;
    }

    public async Task<TaskRecord> Transition(
        string taskId,
        TaskStatus status,
        string? summary = null,
        string? flowId = null,
        CancellationToken ct = default)
    {
        var (index, current) = FindTask(taskId);
        var now = Context.Time.GetUtcNow();
        var updated = current with
        {
            Status = status,
            Summary = summary is null ? current.Summary : summary.Trim(),
            FlowId = string.IsNullOrWhiteSpace(flowId) ? current.FlowId : flowId.Trim(),
            PendingAsk = status == TaskStatus.NeedsYou ? current.PendingAsk : null,
            UpdatedAt = now,
        };

        var tasks = State.Tasks.ToArray();
        tasks[index] = updated;
        var revision = State.Revision + 1;
        await Save(State with { Tasks = tasks, Revision = revision }, new TaskChangedSignal(updated.TaskId, updated.Status, revision)).ConfigureAwait(true);
        return updated;
    }

    public async Task<TaskRecord> Ask(
        string taskId,
        TaskAskPrompt prompt,
        string? confirmationPlanId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ValidatePrompt(prompt);
        var (index, current) = FindTask(taskId);
        var now = Context.Time.GetUtcNow();
        var normalizedPrompt = new TaskAskPrompt(
            prompt.Question.Trim(),
            [.. prompt.Options.Select(static o => o.Trim())],
            prompt.RecommendedIndex);
        var planId = string.IsNullOrWhiteSpace(confirmationPlanId) ? current.ConfirmationPlanId : confirmationPlanId.Trim();

        var updated = current with
        {
            Status = TaskStatus.NeedsYou,
            PendingAsk = normalizedPrompt,
            ConfirmationPlanId = planId,
            UpdatedAt = now,
        };

        var tasks = State.Tasks.ToArray();
        tasks[index] = updated;
        var revision = State.Revision + 1;
        await Save(
            State with { Tasks = tasks, Revision = revision },
            [
                new TaskAskedSignal(updated.TaskId, normalizedPrompt, planId, updated.ConversationId),
                new TaskChangedSignal(updated.TaskId, updated.Status, revision),
            ]).ConfigureAwait(true);
        return updated;
    }

    public async Task<TaskRecord> AnswerAsk(
        string taskId,
        int selectedOptionIndex,
        string? note = null,
        CancellationToken ct = default)
    {
        var caller = RequireSameBrain(() => new UnauthorizedAccessException("Only a signed-in person of this brain may answer a task question."));
        var answerer = Answerer.Of(caller, Id.Brain);
        if (!answerer.IsPerson)
        {
            throw new UnauthorizedAccessException("Only a person may answer a task question (RequiresPerson = true).");
        }

        var (index, current) = FindTask(taskId);
        if (current.PendingAsk is not { } pending)
        {
            throw new InvalidOperationException($"Task '{current.TaskId}' has no pending question to answer.");
        }

        if (selectedOptionIndex < 0 || selectedOptionIndex >= pending.Options.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(selectedOptionIndex), selectedOptionIndex, "Selected option index is out of range.");
        }

        var now = Context.Time.GetUtcNow();
        var chosen = pending.Options[selectedOptionIndex];
        var answer = new TaskAskAnswer(
            SelectedOptionIndex: selectedOptionIndex,
            SelectedOption: chosen,
            AnsweredBy: answerer.Principal.Id,
            AnsweredAt: now,
            Note: string.IsNullOrWhiteSpace(note) ? null : note.Trim());

        var updated = current with
        {
            Status = TaskStatus.InProgress,
            PendingAsk = null,
            LastAnswer = answer,
            UpdatedAt = now,
        };

        var tasks = State.Tasks.ToArray();
        tasks[index] = updated;
        var revision = State.Revision + 1;
        await Save(State with { Tasks = tasks, Revision = revision }, new TaskChangedSignal(updated.TaskId, updated.Status, revision)).ConfigureAwait(true);
        return updated;
    }

    public async Task<TaskRecord> LinkArtifact(
        string taskId,
        TaskArtifactLink artifact,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifact.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifact.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifact.Reference);

        var (index, current) = FindTask(taskId);
        var normalized = new TaskArtifactLink(artifact.Kind.Trim(), artifact.Title.Trim(), artifact.Reference.Trim());
        var existing = current.Artifacts;
        if (existing.Any(a => string.Equals(a.Kind, normalized.Kind, StringComparison.Ordinal)
            && string.Equals(a.Reference, normalized.Reference, StringComparison.Ordinal)))
        {
            return current;
        }

        var updated = current with
        {
            Artifacts = [.. existing, normalized],
            UpdatedAt = Context.Time.GetUtcNow(),
        };

        var tasks = State.Tasks.ToArray();
        tasks[index] = updated;
        var revision = State.Revision + 1;
        await Save(State with { Tasks = tasks, Revision = revision }, new TaskChangedSignal(updated.TaskId, updated.Status, revision)).ConfigureAwait(true);
        return updated;
    }

    public async Task<int> ClearCompleted(CancellationToken ct = default)
    {
        var remaining = State.Tasks.Where(static t => t.Status != TaskStatus.Completed).ToArray();
        var cleared = State.Tasks.Length - remaining.Length;
        if (cleared == 0)
        {
            return 0;
        }

        var revision = State.Revision + 1;
        await Save(State with { Tasks = remaining, Revision = revision }, new TaskChangedSignal("*", TaskStatus.Completed, revision)).ConfigureAwait(true);
        return cleared;
    }

    private (int Index, TaskRecord Task) FindTask(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        var trimmed = taskId.Trim();
        var index = Array.FindIndex(State.Tasks, t => string.Equals(t.TaskId, trimmed, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new KeyNotFoundException($"Task '{trimmed}' was not found.");
        }

        return (index, State.Tasks[index]);
    }

    private static void ValidatePrompt(TaskAskPrompt prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt.Question);
        ArgumentNullException.ThrowIfNull(prompt.Options);
        if (prompt.Options.Length < 2)
        {
            throw new ArgumentException("A task ask prompt requires at least two options.", nameof(prompt));
        }

        if (prompt.Options.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Every task ask option must be non-empty.", nameof(prompt));
        }

        if (prompt.RecommendedIndex is { } rec && (rec < 0 || rec >= prompt.Options.Length))
        {
            throw new ArgumentOutOfRangeException(nameof(prompt), rec, "RecommendedIndex must reference an option in Options.");
        }
    }
}
