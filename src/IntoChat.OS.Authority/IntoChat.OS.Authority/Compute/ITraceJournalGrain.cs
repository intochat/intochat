using IntoChat.OS.Contracts.Compute;

namespace IntoChat.OS.Authority.Compute;

[Alias("compute.trace-journal")]
internal interface ITraceJournalGrain : IGrainWithStringKey
{
    Task Append(Cost cost, long revision);
    Task<ComputeTracePage> Read(int take, string? continuationToken);
    Task Expire();
}
