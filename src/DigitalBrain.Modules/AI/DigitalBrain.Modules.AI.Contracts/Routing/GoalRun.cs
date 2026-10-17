namespace DigitalBrain.AI.Routing;

// One goal the router worked on, and the ordered actions it took to reach it.
[GenerateSerializer, Alias("ai.goal-run")]
public sealed record GoalRun(
    [property: Id(0)] string Goal,
    [property: Id(1)] GoalStep[] Steps,
    [property: Id(2)] string? Answer = null);

[GenerateSerializer, Alias("ai.goal-step")]
public sealed record GoalStep(
    [property: Id(0)] string Contract,
    [property: Id(1)] string Method,
    [property: Id(2)] string? Key,
    [property: Id(3)] string? Arguments,
    [property: Id(4)] string? Result,
    [property: Id(5)] string? Error,
    [property: Id(6)] double Confidence);
