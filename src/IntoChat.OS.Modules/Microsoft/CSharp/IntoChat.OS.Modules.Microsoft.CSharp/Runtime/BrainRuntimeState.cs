using IntoChat.OS.Framework;

namespace IntoChat.OS.Microsoft.CSharp.Runtime;

// A program the brain wants loaded: its compiled synapses, settings and the identity it acts as.
[GenerateSerializer, Alias("microsoft.csharp.program-assignment")]
internal sealed record ProgramAssignment(
    [property: Id(0)] string Owner,
    [property: Id(1)] string Hash,
    [property: Id(2)] Dictionary<string, string> Settings,
    [property: Id(3)] CallerContext? Principal,
    [property: Id(4)] BehaviorAssembly[] Behaviors);

// One synapse of a program: its path within the program and the assembly it compiled to.
[GenerateSerializer, Alias("microsoft.csharp.behavior-assembly")]
internal sealed record BehaviorAssembly([property: Id(0)] string Path, [property: Id(1)] string Hash)
{
    public string Id(string owner) => BehaviorIds.For(owner, Path);
}

// The one session that runs this brain's behaviors. Started is when it was last launched, so a session
// that never connects is relaunched like one that stopped heartbeating; TokenIssued drives rotation.
[GenerateSerializer, Alias("microsoft.csharp.session-record")]
internal sealed record SessionRecord(
    [property: Id(0)] string Id,
    [property: Id(1)] SessionStatus Status,
    [property: Id(2)] string? Fingerprint,
    [property: Id(3)] DateTimeOffset? LastHeartbeat,
    [property: Id(4)] DateTimeOffset Started,
    [property: Id(5)] DateTimeOffset TokenIssued);

[GenerateSerializer, Alias("microsoft.csharp.runtime-state")]
internal sealed record BrainRuntimeState
{
    [Id(0)] public ProgramAssignment[] Programs { get; init; } = [];
    [Id(1)] public Subscription[] Subscriptions { get; init; } = [];
    [Id(2)] public SessionRecord? Session { get; init; }
    [Id(3)] public Dictionary<string, string> Initialized { get; init; } = new(StringComparer.Ordinal);
    [Id(4)] public Dictionary<string, string> AssignmentRevisions { get; init; } = new(StringComparer.Ordinal);
}
