using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Framework;
using Orleans.Concurrency;
using Orleans.Metadata;

namespace DigitalBrain.Microsoft.CSharp.Runtime;

[GenerateSerializer, Alias("microsoft.csharp.session-authorization")]
internal sealed record SessionAuthorization([property: Id(0)] bool Active, [property: Id(1)] string Brain);

[GenerateSerializer, Alias("microsoft.csharp.behavior-identity")]
internal sealed record BehaviorIdentity([property: Id(0)] CallerContext? Principal, [property: Id(1)] string Owner);

// What the behavior neurons and the runtime edge call. Not a neuron contract: no edge resolves it, so
// the call filter never sees it. The session-side operations each check the session id against the
// record: a token outlives a relaunched session only until this check.
// The interleaved ones are pure reads, made for every script call (Authorize, Identity) or by the host and
// the shell: they never wait for a turn in progress. Everything that writes state or the runtime's run
// bookkeeping (PendingBatches starts runs) takes its own turn.
[Alias("microsoft.csharp.runtime-edge"), DefaultGrainType("microsoft.csharp.runtime")]
internal interface IBrainRuntimeEdge : IGrainWithStringKey
{
    [AlwaysInterleave] Task<BehaviorView?> Describe(string behavior);
    // Interleaved: it waits on the host, never on this runtime's state.
    [AlwaysInterleave] Task<string> Logs(string behavior, int tail);
    [AlwaysInterleave] Task<SessionAuthorization> Authorize(string session);
    [AlwaysInterleave] Task<BehaviorIdentity?> Identity(string session, string behavior);
    Task Connected(string session, string fingerprint);
    Task Heartbeat(string session);
    Task<string> Subscribed(string session, string behavior, string neuron, string signal);
    Task Acknowledge(string session, string subscription, long watermark);
    Task AcknowledgeMany(string session, AcknowledgeReport[] acknowledgements);
    Task Reported(string session, BehaviorStatusReport status);
    Task<IReadOnlyList<SignalBatchEvent>> PendingBatches(string session);
    [AlwaysInterleave] Task<IReadOnlyList<AssignmentEvent>> Assignments(string session);
}
