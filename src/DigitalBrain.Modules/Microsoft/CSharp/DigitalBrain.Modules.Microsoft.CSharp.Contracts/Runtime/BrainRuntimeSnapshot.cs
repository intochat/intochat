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
    [property: Id(4)] IReadOnlyList<BehaviorView> Behaviors);

[GenerateSerializer, Alias("microsoft.csharp.behavior-view")]
public sealed record BehaviorView(
    [property: Id(0)] string Id,
    [property: Id(1)] string Hash,
    [property: Id(2)] CSharpFileStatus Status,
    [property: Id(3)] int Failures,
    [property: Id(4)] IReadOnlyList<CSharpSubscriptionView> Subscriptions);
