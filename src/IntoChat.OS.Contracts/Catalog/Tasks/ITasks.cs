using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Signals;

namespace IntoChat.OS.Modules.Apps.Tasks;

/// <summary>Lifecycle status of a durable workspace task.</summary>
public enum TaskStatus
{
    /// <summary>Queued and ready to be picked up.</summary>
    Queued = 0,

    /// <summary>Actively being worked on by a person or worker flow.</summary>
    InProgress = 1,

    /// <summary>Paused waiting for a human decision or multi-option Ask answer.</summary>
    NeedsYou = 2,

    /// <summary>Waiting on an external dependency, child flow, or schedule.</summary>
    Waiting = 3,

    /// <summary>Finished successfully.</summary>
    Completed = 4,

    /// <summary>Stopped due to an error or cancellation.</summary>
    Failed = 5,
}

/// <summary>
/// A person-only multi-option question attached to a task while it is paused in <see cref="TaskStatus.NeedsYou"/>.
/// </summary>
/// <param name="Question">The question shown to the user.</param>
/// <param name="Options">At least two concrete options the user can choose from.</param>
/// <param name="RecommendedIndex">Optional zero-based index into <paramref name="Options"/> for the recommended choice.</param>
[GenerateSerializer, Alias("tasks.ask-prompt")]
public sealed record TaskAskPrompt(
    [property: Id(0)] string Question,
    [property: Id(1)] string[] Options,
    [property: Id(2)] int? RecommendedIndex = null);

/// <summary>
/// A recorded human answer to a <see cref="TaskAskPrompt"/>.
/// </summary>
[GenerateSerializer, Alias("tasks.ask-answer")]
public sealed record TaskAskAnswer(
    [property: Id(0)] int SelectedOptionIndex,
    [property: Id(1)] string SelectedOption,
    [property: Id(2)] string AnsweredBy,
    [property: Id(3)] DateTimeOffset AnsweredAt,
    [property: Id(4)] string? Note = null);

/// <summary>
/// A link to a PR, branch, file, trace, or external artifact produced by a task.
/// </summary>
[GenerateSerializer, Alias("tasks.artifact-link")]
public sealed record TaskArtifactLink(
    [property: Id(0)] string Kind,
    [property: Id(1)] string Title,
    [property: Id(2)] string Reference);

/// <summary>
/// Durable domain record for one workspace task tracked by <see cref="ITasks"/>.
/// </summary>
[GenerateSerializer, Alias("tasks.record")]
public sealed record TaskRecord(
    [property: Id(0)] string TaskId,
    [property: Id(1)] string Title,
    [property: Id(2)] string Description,
    [property: Id(3)] TaskStatus Status,
    [property: Id(4)] string Owner,
    [property: Id(5)] string Tag,
    [property: Id(6)] DateTimeOffset CreatedAt,
    [property: Id(7)] DateTimeOffset UpdatedAt,
    [property: Id(8)] string? FlowId = null,
    [property: Id(9)] string? ConversationId = null,
    [property: Id(10)] string? Summary = null,
    [property: Id(11)] TaskAskPrompt? PendingAsk = null,
    [property: Id(12)] string? ConfirmationPlanId = null,
    [property: Id(13)] TaskAskAnswer? LastAnswer = null,
    TaskArtifactLink[]? Artifacts = null,
    string[]? Contracts = null)
{
    [Id(14)]
    public TaskArtifactLink[] Artifacts { get; init; } = Artifacts ?? [];

    [Id(15)]
    public string[] Contracts { get; init; } = Contracts ?? [];
}

/// <summary>Published whenever a task is created, transitioned, answered, linked, or cleared.</summary>
[Signal("tasks.changed")]
public sealed record TaskChangedSignal(string TaskId, TaskStatus Status, long Revision) : Signal;

/// <summary>Published when a task pauses in <see cref="TaskStatus.NeedsYou"/> with a person-only multi-option question.</summary>
[Signal("tasks.asked")]
public sealed record TaskAskedSignal(
    string TaskId,
    TaskAskPrompt Prompt,
    string? ConfirmationPlanId,
    string? ConversationId) : Signal;

/// <summary>
/// Durable domain contract for workspace tasks backed by <c>TasksNeuron</c>.
/// </summary>
[NeuronAlias("tasks"), DefaultNeuronType("tasks")]
public interface ITasks
{
    const string DefaultKey = "tasks/index";

    static NeuronId Key(string brain) => new(new BrainId(brain), "tasks", DefaultKey);

    /// <summary>Lists tasks without an arbitrary UI cap, optionally filtered by status or owner.</summary>
    [ReadOnlyCall]
    Task<TaskRecord[]> List(TaskStatus? status = null, string? owner = null, CancellationToken ct = default);

    /// <summary>Reads a single task by id, or null when unknown.</summary>
    [ReadOnlyCall]
    Task<TaskRecord?> Get(string taskId, CancellationToken ct = default);

    /// <summary>Creates and enqueues a durable task.</summary>
    Task<TaskRecord> Create(
        string title,
        string? description = null,
        string? owner = null,
        string? tag = null,
        string? flowId = null,
        string? conversationId = null,
        string[]? contracts = null,
        CancellationToken ct = default);

    /// <summary>Transitions a task to a new lifecycle status, optionally recording a summary or linked flow id.</summary>
    Task<TaskRecord> Transition(
        string taskId,
        TaskStatus status,
        string? summary = null,
        string? flowId = null,
        CancellationToken ct = default);

    /// <summary>Pauses a task in <see cref="TaskStatus.NeedsYou"/> with a person-only multi-option question.</summary>
    Task<TaskRecord> Ask(
        string taskId,
        TaskAskPrompt prompt,
        string? confirmationPlanId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Answers a pending multi-option question on a task. Enforces person-only authority: agents and apps are refused.
    /// </summary>
    Task<TaskRecord> AnswerAsk(
        string taskId,
        int selectedOptionIndex,
        string? note = null,
        CancellationToken ct = default);

    /// <summary>Links a PR, branch, file, trace, or other artifact to a task.</summary>
    Task<TaskRecord> LinkArtifact(
        string taskId,
        TaskArtifactLink artifact,
        CancellationToken ct = default);

    /// <summary>Removes all completed tasks and returns how many were cleared.</summary>
    Task<int> ClearCompleted(CancellationToken ct = default);
}
