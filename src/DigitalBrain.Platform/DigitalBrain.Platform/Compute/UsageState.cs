using DigitalBrain.Framework.Compute;

namespace DigitalBrain.Platform.Compute;

[GenerateSerializer, Alias("platform.usage-state")]
internal sealed class UsageState
{
    [Id(0)] public Cost[] Recent { get; set; } = [];
    [Id(1)] public UsageBucket[] Daily { get; set; } = [];
}

// One accumulator per (day, grouping, key); Day is the UTC date in "yyyy-MM-dd" form.
[GenerateSerializer, Alias("platform.usage-bucket")]
internal sealed record UsageBucket(
    [property: Id(0)] string Day,
    [property: Id(1)] UsageGrouping By,
    [property: Id(2)] string Key,
    [property: Id(3)] long Micros,
    [property: Id(4)] long Count);