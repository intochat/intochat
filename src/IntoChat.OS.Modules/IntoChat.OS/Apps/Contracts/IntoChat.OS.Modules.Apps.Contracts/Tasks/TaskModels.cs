namespace IntoChat.OS.Modules.Apps.Contracts.Tasks;

public enum TaskStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    RolledBack = 4,
    Cancelled = 5
}

public enum TaskStepStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    RolledBack = 4
}

[GenerateSerializer, Alias("tasks.step-def")]
public sealed record TaskStepDefinition(
    [property: Id(0)] string Title,
    [property: Id(1)] string Description,
    [property: Id(2)] string Capability,
    [property: Id(3)] string? RollbackCapability = null);

[GenerateSerializer, Alias("tasks.create-request")]
public sealed record CreateTaskRequest(
    [property: Id(0)] string Title,
    [property: Id(1)] string Description,
    [property: Id(2)] string Owner,
    [property: Id(3)] TaskStepDefinition[] Steps);

[GenerateSerializer, Alias("tasks.step")]
public sealed record TaskStep(
    [property: Id(0)] int Index,
    [property: Id(1)] string Title,
    [property: Id(2)] string Description,
    [property: Id(3)] string Capability,
    [property: Id(4)] TaskStepStatus Status,
    [property: Id(5)] string? RollbackCapability = null,
    [property: Id(6)] string? Output = null,
    [property: Id(7)] DateTimeOffset? StartedAt = null,
    [property: Id(8)] DateTimeOffset? CompletedAt = null);

[GenerateSerializer, Alias("tasks.agent-task")]
public sealed record AgentTask(
    [property: Id(0)] string TaskId,
    [property: Id(1)] string Title,
    [property: Id(2)] string Description,
    [property: Id(3)] string Owner,
    [property: Id(4)] TaskStatus Status,
    [property: Id(5)] TaskStep[] Steps,
    [property: Id(6)] DateTimeOffset CreatedAt,
    [property: Id(7)] DateTimeOffset? CompletedAt = null,
    [property: Id(8)] string? RollbackReason = null);

[GenerateSerializer, Alias("tasks.verification-result")]
public sealed record TaskVerificationResult(
    [property: Id(0)] bool Passed,
    [property: Id(1)] string Summary,
    [property: Id(2)] string[]? Violations = null);
