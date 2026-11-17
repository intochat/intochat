using System.ComponentModel;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps.CodeBlocks;

// What sandboxed code may reach through the brain client, and what it did. A need set (the contracts a block's code uses) is approved
// once by a person and remembered by its hash; a deployed block's app key is bound to its run and its granted needs; every call the
// edge sees is recorded here as a step, allowed or refused, so the run can show it without being called back while it is busy.
public sealed record ClientStep(string Call, bool Allowed, DateTimeOffset At);

[NeuronAlias("client.grants"), DefaultNeuronType("client.grants")]
[Description("Grants of the brain client: approved need sets, which sandboxed code may call which contracts, and the calls it made.")]
public interface IClientGrants
{
    const string DefaultKey = "client/grants";

    // The app keys of deployed code blocks start with this; the edge holds their calls to their approved needs.
    const string CodeBlockAppPrefix = "codeblock-";

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<bool> IsApproved(string needsHash);

    Task Approve(string needsHash, string[] needs);

    Task Bind(string appKey, string runId, string[] needs);

    Task Release(string appKey);

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<string[]> Allowed(string appKey);

    Task Record(string appKey, string call, bool allowed);

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<ClientStep[]> Steps(string appKey);
}
