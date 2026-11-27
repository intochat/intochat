using System.Globalization;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Confirmation;
using DigitalBrain.Neurons;

namespace IntoChat.OS.Modules.Apps.Tasks;

/// <summary>
/// Durable Orleans neuron backing <see cref="ITasks"/>. Stores workspace tasks without arbitrary item caps,
/// emits reactive <see cref="TasksChanged"/> / <see cref="TaskAsked"/> signals, and enforces
/// person-only authority on <see cref="AnswerAsk"/>.
/// </summary>
[Neuron("tasks")]
internal sealed class TasksNeuron : Neuron<TasksNeuronState>, ITasks
{
    public Task<TaskRecord[]> List(TaskStatus? status = null)
    {
        IEnumerable<TaskRecord> query = State.Tasks;
        if (status is { } s)
        {
            query = query.Where(t => t.Status == s);
        }

        return Task.FromResult(query.ToArray());
    }

    public Task<TaskRecord?> Read(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        var trimmed = taskId.Trim();
        return Task.FromResult(State.Tasks.FirstOrDefault(t => string.Equals(t.TaskId, trimmed, StringComparison.Ordinal)));
    }

    public async Task<TaskRecord> Create(
        string title,
        string description = "",
        string tag = "Task",
        string? flowId = null,
        string[]? allowedContracts = null)
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

        var resolvedOwner = CallerRules.ActsFor(Context.Caller) ?? "owner";
        var resolvedConversation = Context.Caller?.Conversation;

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
            AllowedContracts: allowedContracts is { Length: > 0 } ? [.. allowedContracts] : []);

        var revision = State.Revision + 1;
        var next = State with
        {
            Tasks = [.. State.Tasks, record],
            Revision = revision,
            NextSequence = seq + 1,
        };
        await Save(next, new TasksChanged(record.TaskId, record.Status)).ConfigureAwait(true);
        return record;
    }

    public async Task<TaskRecord> Transition(
        string taskId,
        TaskStatus status,
        string? summary = null)
    {
        var (index, current) = FindTask(taskId);
        var now = Context.Time.GetUtcNow();
        var updated = current with
        {
            Status = status,
            Summary = summary is null ? current.Summary : summary.Trim(),
            PendingAsk = status == TaskStatus.NeedsYou ? current.PendingAsk : null,
            UpdatedAt = now,
        };

        var tasks = State.Tasks.ToArray();
        tasks[index] = updated;
        var revision = State.Revision + 1;
        await Save(State with { Tasks = tasks, Revision = revision }, new TasksChanged(updated.TaskId, updated.Status)).ConfigureAwait(true);
        return updated;
    }

    public async Task<TaskRecord> Ask(
        string taskId,
        TaskAskPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ValidatePrompt(prompt);
        var (index, current) = FindTask(taskId);
        var now = Context.Time.GetUtcNow();
        var normalizedOptions = prompt.Options.Select(static o => o.Trim()).ToArray();
        var normalizedPrompt = new TaskAskPrompt(
            prompt.Question.Trim(),
            normalizedOptions,
            prompt.RecommendedIndex);

        var updated = current with
        {
            Status = TaskStatus.NeedsYou,
            PendingAsk = normalizedPrompt,
            UpdatedAt = now,
        };

        var tasks = State.Tasks.ToArray();
        tasks[index] = updated;
        var revision = State.Revision + 1;
        await Save(
            State with { Tasks = tasks, Revision = revision },
            [
                new TaskAsked(updated.TaskId, normalizedPrompt.Question, normalizedPrompt.Options, normalizedPrompt.RecommendedIndex),
                new TasksChanged(updated.TaskId, updated.Status),
            ]).ConfigureAwait(true);
        return updated;
    }

    public async Task<TaskRecord> AnswerAsk(
        string taskId,
        string selectedOption)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedOption);
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

        var trimmedOption = selectedOption.Trim();
        var optionIndex = Array.FindIndex(pending.Options, o => string.Equals(o, trimmedOption, StringComparison.OrdinalIgnoreCase));
        if (optionIndex < 0)
        {
            throw new ArgumentException($"Option '{trimmedOption}' is not one of the choices for task '{current.TaskId}'.", nameof(selectedOption));
        }

        var now = Context.Time.GetUtcNow();
        var answer = new TaskAskAnswer(
            SelectedOptionIndex: optionIndex,
            SelectedOption: pending.Options[optionIndex],
            AnsweredBy: answerer.Principal.Id,
            AnsweredAt: now);

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
        await Save(State with { Tasks = tasks, Revision = revision }, new TasksChanged(updated.TaskId, updated.Status)).ConfigureAwait(true);
        return updated;
    }

    public async Task<TaskRecord> LinkArtifact(
        string taskId,
        TaskArtifactLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentException.ThrowIfNullOrWhiteSpace(link.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(link.Label);
        ArgumentException.ThrowIfNullOrWhiteSpace(link.Uri);

        var (index, current) = FindTask(taskId);
        var normalized = new TaskArtifactLink(
            link.Kind.Trim(),
            link.Label.Trim(),
            link.Uri.Trim(),
            string.IsNullOrWhiteSpace(link.CommitSha) ? null : link.CommitSha.Trim());
        var existing = current.Artifacts;
        if (existing.Any(a => string.Equals(a.Kind, normalized.Kind, StringComparison.Ordinal)
            && string.Equals(a.Uri, normalized.Uri, StringComparison.Ordinal)))
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
        await Save(State with { Tasks = tasks, Revision = revision }, new TasksChanged(updated.TaskId, updated.Status)).ConfigureAwait(true);
        return updated;
    }

    public async Task<int> ClearCompleted()
    {
        var remaining = State.Tasks.Where(static t => t.Status != TaskStatus.Completed).ToArray();
        var cleared = State.Tasks.Length - remaining.Length;
        if (cleared == 0)
        {
            return 0;
        }

        var revision = State.Revision + 1;
        await Save(State with { Tasks = remaining, Revision = revision }, new TasksChanged("*", TaskStatus.Completed)).ConfigureAwait(true);
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
