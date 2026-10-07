using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp.Files;

namespace DigitalBrain.Microsoft.CSharp.Runtime;

// A behavior the brain wants loaded: its compiled assembly, settings and the identity it acts as.
[GenerateSerializer, Alias("microsoft.csharp.behavior-assignment")]
internal sealed record BehaviorAssignment(
    [property: Id(0)] string Id,
    [property: Id(1)] string Hash,
    [property: Id(2)] Dictionary<string, string> Settings,
    [property: Id(3)] CallerContext? Owner,
    [property: Id(4)] string? AppId,
    [property: Id(5)] CSharpFileStatus Status,
    [property: Id(6)] int? ExitCode,
    [property: Id(7)] int Failures);

// The one session that runs this brain's behaviors. Started is when it was last launched, so a session
// that never connects is relaunched like one that stopped heartbeating.
[GenerateSerializer, Alias("microsoft.csharp.session-record")]
internal sealed record SessionRecord(
    [property: Id(0)] string Id,
    [property: Id(1)] SessionStatus Status,
    [property: Id(2)] string? Fingerprint,
    [property: Id(3)] DateTimeOffset? LastHeartbeat,
    [property: Id(4)] DateTimeOffset Started);

[GenerateSerializer, Alias("microsoft.csharp.runtime-state")]
internal sealed record BrainRuntimeState
{
    [Id(0)] public BehaviorAssignment[] Behaviors { get; init; } = [];
    [Id(1)] public Subscription[] Subscriptions { get; init; } = [];
    [Id(2)] public SessionRecord? Session { get; init; }
}
