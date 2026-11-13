namespace IntoChat.OS.Modules.Work;

/// <summary>
/// A single planned task within a decomposed work plan.
/// </summary>
public sealed record WorkTask(
    string TaskId,
    string Title,
    string Type,
    IReadOnlyList<string> DependsOn,
    string InnerJson = "{}");

/// <summary>
/// A decomposed execution plan for complex orchestrations.
/// </summary>
public sealed record WorkPlan(
    string PlanId,
    string Title,
    string Goal,
    IReadOnlyList<WorkTask> Tasks);
