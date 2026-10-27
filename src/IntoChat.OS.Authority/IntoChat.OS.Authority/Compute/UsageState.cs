using IntoChat.OS.Contracts.Compute;

namespace IntoChat.OS.Authority.Compute;

[GenerateSerializer, Alias("platform.usage-state")]
internal sealed class UsageState
{
    [Id(0)] public Cost[] Recent { get; set; } = [];
    [Id(1)] public UsageBucket[] Daily { get; set; } = [];
    [Id(2)] public long AppliedSequence { get; set; }
}

// One accumulator per (day, grouping, key); Day is the UTC date in "yyyy-MM-dd" form.
[GenerateSerializer, Alias("platform.usage-bucket")]
internal sealed record UsageBucket(
    [property: Id(0)] string Day,
    [property: Id(1)] UsageGrouping By,
    [property: Id(2)] string Key,
    [property: Id(3)] long Micros,
    [property: Id(4)] long Count)
{
    [Id(5)] public long? KnownListPriceMicros { get; init; }
    [Id(6)] public long? UnpricedCount { get; init; }
    [Id(7)] public long? PendingCount { get; init; }
    [Id(8)] public long? UnknownCount { get; init; }
}
