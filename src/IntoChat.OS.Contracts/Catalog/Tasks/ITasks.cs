using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Signals;
using IntoChat.OS.Host.Surface;

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
    [property: Id(3)] DateTimeOffset AnsweredAt);

/// <summary>
/// A link to a PR, branch, file, trace, or external artifact produced by a task.
/// </summary>
[GenerateSerializer, Alias("tasks.artifact-link")]
public sealed record TaskArtifactLink(
    [property: Id(0)] string Kind,
    [property: Id(1)] string Label,
    [property: Id(2)] string Uri,
    [property: Id(3)] string? CommitSha = null);

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
    [property: Id(12)] TaskAskAnswer? LastAnswer = null,
    TaskArtifactLink[]? Artifacts = null,
    string[]? AllowedContracts = null)
{
    [Id(13)]
    public TaskArtifactLink[] Artifacts { get; init; } = Artifacts ?? [];

    [Id(14)]
    public string[] AllowedContracts { get; init; } = AllowedContracts ?? [];
}

/// <summary>Published whenever a task is created, transitioned, answered, linked, or cleared.</summary>
[Signal("tasks.changed")]
public sealed record TasksChanged(string TaskId, TaskStatus Status) : Signal;

/// <summary>Published when a task pauses in <see cref="TaskStatus.NeedsYou"/> with a person-only multi-option question.</summary>
[Signal("tasks.asked")]
public sealed record TaskAsked(
    string TaskId,
    string Question,
    string[] Options,
    int? RecommendedIndex) : Signal;

/// <summary>
/// Durable domain contract for workspace tasks backed by <c>TasksNeuron</c>.
/// </summary>
[NeuronAlias("tasks"), DefaultNeuronType("tasks")]
public interface ITasks
{
    const string DefaultKey = "tasks/board";

    static NeuronId Key(BrainId brain) => new(brain, "tasks", DefaultKey);

    /// <summary>Lists tasks without an arbitrary UI cap, optionally filtered by status.</summary>
    [ReadOnlyCall, Interleaves]
    Task<TaskRecord[]> List(TaskStatus? status = null);

    /// <summary>Reads a single task by id, or null when unknown.</summary>
    [ReadOnlyCall, Interleaves]
    Task<TaskRecord?> Read(string taskId);

    /// <summary>Creates and enqueues a durable task.</summary>
    Task<TaskRecord> Create(
        string title,
        string description = "",
        string tag = "Task",
        string? flowId = null,
        string[]? allowedContracts = null);

    /// <summary>Transitions a task to a new lifecycle status, optionally recording a summary.</summary>
    Task<TaskRecord> Transition(
        string taskId,
        TaskStatus status,
        string? summary = null);

    /// <summary>Pauses a task in <see cref="TaskStatus.NeedsYou"/> with a person-only multi-option question.</summary>
    Task<TaskRecord> Ask(
        string taskId,
        TaskAskPrompt prompt);

    /// <summary>
    /// Answers a pending multi-option question on a task. Enforces person-only authority: agents and apps are refused.
    /// </summary>
    [PersonOnly]
    Task<TaskRecord> AnswerAsk(
        string taskId,
        string selectedOption);

    /// <summary>Links a PR, branch, file, trace, or other artifact to a task.</summary>
    Task<TaskRecord> LinkArtifact(
        string taskId,
        TaskArtifactLink link);

    /// <summary>Removes all completed tasks and returns how many were cleared.</summary>
    Task<int> ClearCompleted();
}
