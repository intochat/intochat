using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Framework;
using Orleans.Concurrency;
using Orleans.Metadata;

namespace DigitalBrain.Microsoft.CSharp.Runtime;

[GenerateSerializer, Alias("microsoft.csharp.session-authorization")]
internal sealed record SessionAuthorization([property: Id(0)] bool Active, [property: Id(1)] string Brain);

[GenerateSerializer, Alias("microsoft.csharp.behavior-identity")]
internal sealed record BehaviorIdentity([property: Id(0)] CallerContext? Owner, [property: Id(1)] string? AppId);

// What the behavior neurons and the runtime edge call. The session-side operations each check the
// session id against the record: a token outlives a relaunched session only until this check.
[Alias("microsoft.csharp.runtime-edge"), DefaultGrainType("microsoft.csharp.runtime")]
internal interface IBrainRuntimeEdge : IGrainWithStringKey
{
    Task Assign(string behavior, string hash, Dictionary<string, string> settings, CallerContext? owner, string? appId);
    Task Retire(string behavior);
    Task<BehaviorView?> Describe(string behavior);
    // Interleaved: it waits on the host, never on this runtime's state.
    [AlwaysInterleave] Task<string> Logs(string behavior, int tail);
    Task<SessionAuthorization> Authorize(string session);
    Task<BehaviorIdentity?> Identity(string session, string behavior);
    Task Connected(string session, string fingerprint);
    Task Heartbeat(string session);
    Task<string> Subscribed(string session, string behavior, string neuron, string signal);
    Task Acknowledge(string session, string subscription, long watermark);
    Task AcknowledgeMany(string session, AcknowledgeReport[] acknowledgements);
    Task Reported(string session, BehaviorStatusReport status);
    Task<IReadOnlyList<SignalBatchEvent>> PendingBatches(string session);
    Task<IReadOnlyList<AssignmentEvent>> Assignments(string session);
}
