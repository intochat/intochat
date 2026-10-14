using DigitalBrain.Microsoft.CSharp.Files;

namespace DigitalBrain.Microsoft.CSharp.Runtime;

[GenerateSerializer, Alias("microsoft.csharp.session-status")]
public enum SessionStatus { None, Starting, Connected, Refused }

[GenerateSerializer, Alias("microsoft.csharp.runtime-snapshot")]
public sealed record BrainRuntimeSnapshot(
    [property: Id(0)] SessionStatus Session,
    [property: Id(1)] string? SessionId,
    [property: Id(2)] string? Fingerprint,
    [property: Id(3)] DateTimeOffset? LastHeartbeat,
    [property: Id(4)] IReadOnlyList<BehaviorView> Behaviors,
    [property: Id(5)] bool IsReady = false,
    // This activation's ready persistent scripts and queued pricing have settled; other runtimes may still work.
    [property: Id(6)] bool IsIdle = false);

// Runs counts the behavior's finished runs, succeeded or failed, Failures the failed ones, and
// LastDurationMicroseconds is the last success's time: what this runtime activation saw of this Hash, so a
// reactivation or a rewrite starts again.
[GenerateSerializer, Alias("microsoft.csharp.behavior-view")]
public sealed record BehaviorView(
    [property: Id(0)] string Id,
    [property: Id(1)] string Hash,
    [property: Id(2)] string Status,
    [property: Id(3)] string? Error,
    [property: Id(4)] CSharpSubscriptionView[] Subscriptions,
    [property: Id(5)] long Runs = 0,
    [property: Id(6)] long Failures = 0,
    [property: Id(7)] long LastDurationMicroseconds = 0);
