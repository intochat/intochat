using IntoChat.OS.Contracts.Compute;

namespace IntoChat.OS.Authority.Compute;

[Alias("compute.brain")]
internal interface IBrainComputeGrain : IGrainWithStringKey
{
    Task<ExternalCallHandle> Begin(PinnedExternalCall request);
    Task MarkDispatchStarted(ExternalCallHandle handle);
    Task<ExternalCallCompletion> Observe(ExternalCallHandle handle, ExternalCallOutcome outcome);
    Task<Cost> Record(Trace trace, Cost cost, Funding funding, string origin);
    Task Reconcile();
    Task<ComputeTracePage> ReadTrace(string traceId, int take, string? continuationToken);
}
