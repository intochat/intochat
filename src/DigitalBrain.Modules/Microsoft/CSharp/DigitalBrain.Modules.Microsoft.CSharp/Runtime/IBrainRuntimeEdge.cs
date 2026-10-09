using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Framework;
using Orleans.Concurrency;
using Orleans.Metadata;

namespace DigitalBrain.Microsoft.CSharp.Runtime;

[GenerateSerializer, Alias("microsoft.csharp.session-authorization")]
internal sealed record SessionAuthorization([property: Id(0)] bool Active, [property: Id(1)] string Brain);

[GenerateSerializer, Alias("microsoft.csharp.behavior-identity")]
internal sealed record BehaviorIdentity([property: Id(0)] CallerContext? Principal, [property: Id(1)] string Owner);

// What the behavior neurons and the runtime edge call. The session-side operations each check the
// session id against the record: a token outlives a relaunched session only until this check.
// They are public because the edge validated the session token before any of them.
[Alias("microsoft.csharp.runtime-edge"), DefaultGrainType("microsoft.csharp.runtime")]
internal interface IBrainRuntimeEdge : IGrainWithStringKey
{
    Task<BehaviorView?> Describe(string behavior);
    // Interleaved: it waits on the host, never on this runtime's state.
    [AlwaysInterleave] Task<string> Logs(string behavior, int tail);
    [DigitalBrain.Public] Task<SessionAuthorization> Authorize(string session);
    [DigitalBrain.Public] Task<BehaviorIdentity?> Identity(string session, string behavior);
    [DigitalBrain.Public] Task Connected(string session, string fingerprint);
    [DigitalBrain.Public] Task Heartbeat(string session);
    [DigitalBrain.Public] Task<string> Subscribed(string session, string behavior, string neuron, string signal);
    [DigitalBrain.Public] Task Acknowledge(string session, string subscription, long watermark);
    [DigitalBrain.Public] Task AcknowledgeMany(string session, AcknowledgeReport[] acknowledgements);
    [DigitalBrain.Public] Task Reported(string session, BehaviorStatusReport status);
    [DigitalBrain.Public] Task<IReadOnlyList<SignalBatchEvent>> PendingBatches(string session);
    [DigitalBrain.Public] Task<IReadOnlyList<AssignmentEvent>> Assignments(string session);
}
