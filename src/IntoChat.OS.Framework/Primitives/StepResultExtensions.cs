using DigitalBrain;
using DigitalBrain.Flows;

namespace IntoChat.OS.Primitives;

/// <summary>
/// Fluent helpers for constructing <see cref="StepResult{T}"/> values in durable OS flows.
/// </summary>
public static class StepResultExtensions
{
    public static StepResult<T> Advance<T>(this T inner, params Signal[] signals)
        => signals.Length == 0 ? new(inner) : new(inner, null, signals);

    public static StepResult<T> WaitExternal<T>(this T inner, string step, string? confirmationPlanId = null, params Signal[] signals)
        => new(inner, new Waiting(step, WaitReason.External, null, confirmationPlanId), signals.Length == 0 ? null : signals);

    public static StepResult<T> WaitApproval<T>(this T inner, string step, DateTimeOffset? expires = null, string? confirmationPlanId = null, params Signal[] signals)
        => new(inner, new Waiting(step, WaitReason.Approval, expires, confirmationPlanId), signals.Length == 0 ? null : signals);

    public static StepResult<T> PollAt<T>(this T inner, string step, DateTimeOffset wakeAt, params Signal[] signals)
        => new(inner, new Waiting(step, WaitReason.Schedule, wakeAt, null), signals.Length == 0 ? null : signals);
}
