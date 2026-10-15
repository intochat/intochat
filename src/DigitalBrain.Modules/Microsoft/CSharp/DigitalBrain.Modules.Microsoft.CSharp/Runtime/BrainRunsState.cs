using DigitalBrain.Microsoft.CSharp.Runtime.Signals;
using DigitalBrain.Framework.Compute;

namespace DigitalBrain.Microsoft.CSharp.Runtime;

[GenerateSerializer, Alias("microsoft.csharp.runs-state")]
internal sealed record BrainRunsState
{
    [Id(0)] public long Revision { get; init; }
    [Id(1)] public long Sequence { get; init; }
    [Id(2)] public BrainRunEntry[] Recent { get; init; } = [];
    [Id(3)] public BrainRunStatistics[] Statistics { get; init; } = [];

    public BrainRunsState Apply(RunFact[] facts)
    {
        var recent = Recent.ToList();
        var statistics = Statistics.ToDictionary(item => item.Behavior, StringComparer.Ordinal);
        var sequence = Sequence;
        var changed = false;
        foreach (var fact in facts)
        {
            if (fact.Fact is not BehaviorRan ran || ran.Outcome == RunOutcome.Skipped) { continue; }
            var mark = $"{ran.Behavior}|{ran.Source}|{ran.SignalType}|{ran.Watermark}|{ran.Outcome}";
            if (!recent.Any(item => item.Mark == mark))
            {
                recent.Add(new(++sequence, ran.Owner, ran.Behavior, ran.Outcome, ran.DurationMicroseconds,
                    (fact.Trace ?? ran.Trace ?? TraceContext.Current)?.TraceId, mark));
                var excess = recent.Count(item => item.Owner == ran.Owner) - 200;
                if (excess > 0)
                {
                    var remove = recent.Where(item => item.Owner == ran.Owner).Take(excess).Select(item => item.Sequence).ToHashSet();
                    recent.RemoveAll(item => remove.Contains(item.Sequence));
                }
                changed = true;
            }
            if (ran.Outcome != RunOutcome.Succeeded || ran.DurationMicroseconds <= 0) { continue; }
            statistics.TryGetValue(ran.Behavior, out var previous);
            BrainRunSample[] samples = previous?.Hash == ran.Hash ? previous.Samples : [];
            var sampleMark = $"{ran.Source}|{ran.SignalType}|{ran.Watermark}";
            if (samples.Any(sample => sample.Mark == sampleMark)) { continue; }
            statistics[ran.Behavior] = new(ran.Behavior, ran.Hash,
                [.. samples.TakeLast(30), new(sampleMark, ran.DurationMicroseconds)]);
            changed = true;
        }
        return changed ? new BrainRunsState
        {
            Revision = Revision + 1,
            Sequence = sequence,
            Recent = [.. recent],
            Statistics = [.. statistics.Values.OrderBy(item => item.Behavior, StringComparer.Ordinal)],
        } : this;
    }
}
