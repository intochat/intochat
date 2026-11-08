using DigitalBrain;

// legacy, read-only, delete after the next release
// A copy of the deleted task engine's stored records (AI module, TaskState.cs and TaskModels.cs): same names, [Id]s, aliases and
// enum values, so a brain that ran the old code still decodes them. The neurons below answer on the old types ("tasks.task" with
// state "task", "tasks.directory" with state "tasks-dir"); MigrateTasksFlow reads them and marks each task Archived, nothing else.
namespace IntoChat.OS.Modules.Apps.Flows.Legacy;

public enum LegacyTaskStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    RolledBack = 4,
    Cancelled = 5,
    AwaitingConfirmation = 6,
}

public enum LegacyTaskStepStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    RolledBack = 4,
    AwaitingConfirmation = 5,
}

[GenerateSerializer, Alias("tasks.state")]
public sealed class LegacyTaskState
{
    [Id(0)] public string TaskId { get; set; } = "";
    [Id(1)] public string Title { get; set; } = "";
    [Id(2)] public string Description { get; set; } = "";
    [Id(3)] public string Owner { get; set; } = "";
    [Id(4)] public LegacyTaskStatus Status { get; set; } = LegacyTaskStatus.Pending;
    [Id(5)] public LegacyTaskStepState[] Steps { get; set; } = [];
    [Id(6)] public DateTimeOffset CreatedAt { get; set; }
    [Id(7)] public DateTimeOffset? CompletedAt { get; set; }
    [Id(8)] public string? RollbackReason { get; set; }
    [Id(9)] public string? ParentId { get; set; }
    [Id(10)] public LegacyTaskLinkState[] Links { get; set; } = [];
    // Added by the migration: the task became a note flow and is kept only for this release.
    [Id(11)] public bool Archived { get; set; }
}

[GenerateSerializer, Alias("tasks.link-state")]
public sealed class LegacyTaskLinkState
{
    [Id(0)] public string TargetId { get; set; } = "";
    [Id(1)] public string Kind { get; set; } = "";
}

[GenerateSerializer, Alias("tasks.step-state")]
public sealed class LegacyTaskStepState
{
    [Id(0)] public int Index { get; set; }
    [Id(1)] public string Title { get; set; } = "";
    [Id(2)] public string Description { get; set; } = "";
    [Id(3)] public string Capability { get; set; } = "";
    [Id(4)] public LegacyTaskStepStatus Status { get; set; } = LegacyTaskStepStatus.Pending;
    [Id(5)] public string? RollbackCapability { get; set; }
    [Id(6)] public string? Output { get; set; }
    [Id(7)] public DateTimeOffset? StartedAt { get; set; }
    [Id(8)] public DateTimeOffset? CompletedAt { get; set; }
    [Id(9)] public string? IdempotencyKey { get; set; }
}

[GenerateSerializer, Alias("tasks.directory-state")]
public sealed class LegacyTaskDirectoryState
{
    [Id(0)] public LegacyTaskDirectoryEntryState[] Entries { get; set; } = [];
}

[GenerateSerializer, Alias("tasks.directory-entry")]
public sealed class LegacyTaskDirectoryEntryState
{
    [Id(0)] public string TaskId { get; set; } = "";
    [Id(1)] public string Title { get; set; } = "";
    [Id(2)] public string Description { get; set; } = "";
    [Id(3)] public string Owner { get; set; } = "";
    [Id(4)] public LegacyTaskStatus Status { get; set; } = LegacyTaskStatus.Pending;
    [Id(5)] public DateTimeOffset CreatedAt { get; set; }
    [Id(6)] public DateTimeOffset? CompletedAt { get; set; }
    [Id(7)] public int StepCount { get; set; }
    [Id(8)] public int CompletedStepCount { get; set; }
    [Id(9)] public string? ParentId { get; set; }
    [Id(10)] public LegacyTaskLinkState[] Links { get; set; } = [];
    [Id(11)] public LegacyTaskStepState[] Steps { get; set; } = [];
}

// The old task became a note flow.
[Signal("tasks.archived")]
public sealed record LegacyTaskArchived(string TaskId) : Signal;

// The old task neuron's stored record, read through its old type; Archive is the only write.
public interface ILegacyTaskReader
{
    static NeuronId Key(BrainId brain, string taskId) => new(brain, LegacyTaskNeuron.Type, taskId);

    [ReadOnlyCall]
    Task<LegacyTaskState> Read();

    Task Archive();
}

// The old task directory's stored record, read through its old type.
public interface ILegacyTaskDirectory
{
    static NeuronId Key(BrainId brain) => new(brain, LegacyTaskDirectoryNeuron.Type, "default");

    [ReadOnlyCall]
    Task<LegacyTaskDirectoryState> Read();
}

[Neuron(Type, State = "task")]
internal sealed class LegacyTaskNeuron : Neuron<LegacyTaskState>, ILegacyTaskReader
{
    public const string Type = "tasks.task";

    public Task<LegacyTaskState> Read() => Task.FromResult(State);

    // A task that never existed has nothing to archive; saving it would create a record.
    public Task Archive() => State.Archived || State.TaskId.Length == 0 ? Task.CompletedTask : Save(new LegacyTaskState
    {
        TaskId = State.TaskId, Title = State.Title, Description = State.Description, Owner = State.Owner, Status = State.Status,
        Steps = State.Steps, CreatedAt = State.CreatedAt, CompletedAt = State.CompletedAt, RollbackReason = State.RollbackReason,
        ParentId = State.ParentId, Links = State.Links, Archived = true,
    }, new LegacyTaskArchived(State.TaskId));
}

[Neuron(Type, State = "tasks-dir")]
internal sealed class LegacyTaskDirectoryNeuron : Neuron<LegacyTaskDirectoryState>, ILegacyTaskDirectory
{
    public const string Type = "tasks.directory";

    public Task<LegacyTaskDirectoryState> Read() => Task.FromResult(State);
}
