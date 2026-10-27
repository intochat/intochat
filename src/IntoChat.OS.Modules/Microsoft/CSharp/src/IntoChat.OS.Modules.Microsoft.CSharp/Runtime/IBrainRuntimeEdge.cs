using IntoChat.OS.Client.Runtime.V1;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Runtime;

internal sealed record SessionAuthorization(bool Active, string Brain);

internal sealed record BehaviorIdentity(CallerContext? Principal, string Owner)
{
    // What a behavior's calls and its subscriptions' watches carry: the app's own principal behind the app proxy,
    // acting on behalf of the person who assigned the program. A program assigned without a caller (tests, the
    // platform) stamps none.
    public CallerContext? Caller => Principal is { } principal
        ? principal with
        {
            PrincipalId = AppPrincipal.Id(Owner),
            OnBehalfOf = principal.PersonId,
            Kind = CallerKind.App,
            StampedBy = TrustedEdge.AppProxy,
            AppId = Owner,
        }
        : null;
}

// What the behavior neurons and the runtime edge call. Not a neuron contract: no edge resolves it, so
// the call filter never sees it. The session-side operations each check the session id against the
// record: a token outlives a relaunched session only until this check.
// The interleaved ones are pure reads, made for every script call (Authorize, Identity) or by the host and
// the shell: they never wait for a turn in progress. Everything that writes state or the runtime's run
// bookkeeping (PendingBatches starts runs) takes its own turn.
[DigitalBrain.Contracts.NeuronAlias("microsoft.csharp.runtime-edge"), DigitalBrain.Contracts.DefaultNeuronType("microsoft.csharp.runtime")]
internal interface IBrainRuntimeEdge
{
    [DigitalBrain.Interleaves] Task<BehaviorView?> Describe(string behavior);
    // Interleaved: it waits on the host, never on this runtime's state.
    [DigitalBrain.Interleaves] Task<string> Logs(string behavior, int tail);
    [DigitalBrain.Interleaves] Task<SessionAuthorization> Authorize(string session);
    [DigitalBrain.Interleaves] Task<BehaviorIdentity?> Identity(string session, string behavior);
    Task Connected(string session, string fingerprint);
    Task Heartbeat(string session);
    Task<string> Subscribed(string session, string behavior, string neuron, string signal);
    Task<string> Subscribed(string session, string behavior, string neuron, string signal, string? selector);
    Task Acknowledge(string session, string subscription, long watermark);
    Task AcknowledgeMany(string session, AcknowledgeReport[] acknowledgements);
    Task Reported(string session, BehaviorStatusReport status);
    Task<IReadOnlyList<SignalBatchEvent>> PendingBatches(string session);
    [DigitalBrain.Interleaves] Task<IReadOnlyList<AssignmentEvent>> Assignments(string session);
}
