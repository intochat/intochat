namespace IntoChat.OS.Framework.Compute;

public enum TraceDetailStatus
{
    Complete = 0,
    Incomplete = 1,
    Expired = 2,
    DetailUnavailable = 3,
}

[GenerateSerializer, Alias("compute.trace-page")]
public sealed record ComputeTracePage
{
    [Id(0)] public Cost[] Rows { get; init; } = [];
    [Id(1)] public long ChargedMicros { get; init; }
    [Id(2)] public long KnownListPriceMicros { get; init; }
    [Id(3)] public long UnpricedCount { get; init; }
    [Id(4)] public long PendingCount { get; init; }
    [Id(5)] public long UnknownCount { get; init; }
    [Id(6)] public long SnapshotWatermark { get; init; }
    [Id(7)] public string? ContinuationToken { get; init; }
    [Id(8)] public TraceDetailStatus Status { get; init; }
}
