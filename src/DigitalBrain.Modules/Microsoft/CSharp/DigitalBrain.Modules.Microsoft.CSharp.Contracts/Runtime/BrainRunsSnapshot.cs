using DigitalBrain.Microsoft.CSharp.Runtime.Signals;

namespace DigitalBrain.Microsoft.CSharp.Runtime;

// Recent is the latest 200 entries after excluding a whole owner, ordered oldest first.
[GenerateSerializer, Alias("microsoft.csharp.runs-snapshot")]
public sealed record BrainRunsSnapshot(
    [property: Id(0)] long Revision,
    [property: Id(1)] BrainRunEntry[] Recent,
    [property: Id(2)] BrainRunStatistics[] Statistics);

[GenerateSerializer, Alias("microsoft.csharp.run-entry")]
public sealed record BrainRunEntry(
    [property: Id(0)] long Sequence,
    [property: Id(1)] string Owner,
    [property: Id(2)] string Behavior,
    [property: Id(3)] RunOutcome Outcome,
    [property: Id(4)] long DurationMicroseconds,
    [property: Id(5)] string? TraceId,
    [property: Id(6)] string Mark);

[GenerateSerializer, Alias("microsoft.csharp.run-sample")]
public sealed record BrainRunSample(
    [property: Id(0)] string Mark,
    [property: Id(1)] long Microseconds);

[GenerateSerializer, Alias("microsoft.csharp.run-statistics")]
public sealed record BrainRunStatistics(
    [property: Id(0)] string Behavior,
    [property: Id(1)] string Hash,
    [property: Id(2)] BrainRunSample[] Samples)
{
    public long P50Microseconds => Samples.Length == 0 ? 0
        : Samples.Select(sample => sample.Microseconds).Order().ElementAt((Samples.Length - 1) / 2);
}
