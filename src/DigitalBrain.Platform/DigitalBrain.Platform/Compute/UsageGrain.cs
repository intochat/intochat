using System.Globalization;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Compute;
using Orleans.Concurrency;

namespace DigitalBrain.Platform.Compute;

// Per brain. Every cost lands in the Recent ring buffer and rolls into one daily bucket per
// grouping, so Group aggregates buckets instead of scanning the ring. Every metered call of the brain
// lands here, so costs come in bursts: reentrant, each Add applies its cost at once and shares the write
// that is out (CoalescedWrites), and the reads answer from memory without waiting behind a write.
[GrainType("usage")]
[Reentrant]
internal sealed class UsageGrain(
    [PersistentState("usage", DigitalBrainNames.PlatformGrainStorage)] IPersistentState<UsageState> store)
    : Grain, IUsageGrain
{
    // The ring holds what GET /compute/recent serves at most; the totals come from the daily buckets.
    internal const int RecentLimit = ComputeEndpoints.MaximumTake;
    private const int DailyRetentionDays = 90;
    private CoalescedWrites? _writes;

    private CoalescedWrites Writes => _writes ??= new(store.WriteStateAsync);

    public Task Add(Cost cost, string origin)
    {
        ArgumentNullException.ThrowIfNull(cost);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        store.State.Recent = Push(store.State.Recent, cost);
        var at = cost.At == default ? DateTimeOffset.UtcNow : cost.At;
        var day = at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var by in Groupings)
        {
            Upsert(day, by, KeyFor(cost, by, origin), cost.Micros);
        }
        Prune();
        return Writes.Save();
    }

    public Task<CostRow[]> Group(UsageGrouping by, DateTimeOffset since, int take)
    {
        if (take < 0) { throw new ArgumentException("take must not be negative.", nameof(take)); }
        var sinceDay = since.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var totals = new Dictionary<string, (long Micros, long Count)>();
        foreach (var bucket in store.State.Daily)
        {
            if (bucket.By != by || string.CompareOrdinal(bucket.Day, sinceDay) < 0) { continue; }
            totals.TryGetValue(bucket.Key, out var existing);
            totals[bucket.Key] = (existing.Micros + bucket.Micros, existing.Count + bucket.Count);
        }
        var rows = totals
            .Select(pair => new CostRow(pair.Key, pair.Value.Micros, pair.Value.Count))
            .OrderByDescending(row => row.Micros)
            .ThenBy(row => row.Key, StringComparer.Ordinal)
            .Take(take)
            .ToArray();
        return Task.FromResult(rows);
    }

    public Task<Cost[]> Recent(int take)
    {
        if (take < 0) { throw new ArgumentException("take must not be negative.", nameof(take)); }
        return Task.FromResult<Cost[]>([.. store.State.Recent.Reverse().Take(take)]);
    }

    private void Upsert(string day, UsageGrouping by, string key, long micros)
    {
        for (var i = 0; i < store.State.Daily.Length; i++)
        {
            var bucket = store.State.Daily[i];
            if (bucket.By == by && string.Equals(bucket.Key, key, StringComparison.Ordinal) && string.Equals(bucket.Day, day, StringComparison.Ordinal))
            {
                store.State.Daily[i] = bucket with { Micros = bucket.Micros + micros, Count = bucket.Count + 1 };
                return;
            }
        }
        store.State.Daily = [.. store.State.Daily, new UsageBucket(day, by, key, micros, 1)];
    }

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-DailyRetentionDays).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var kept = store.State.Daily.Where(bucket => string.CompareOrdinal(bucket.Day, cutoff) >= 0).ToArray();
        if (kept.Length != store.State.Daily.Length) { store.State.Daily = kept; }
    }

    private static string KeyFor(Cost cost, UsageGrouping by, string origin) => by switch
    {
        UsageGrouping.Origin => origin,
        UsageGrouping.App => cost.App ?? "",
        UsageGrouping.Synapse => cost.Synapse ?? "",
        UsageGrouping.Neuron => cost.Neuron ?? "",
        UsageGrouping.Kind => cost.Kind.ToString(),
        _ => throw new ArgumentOutOfRangeException(nameof(by)),
    };

    private static Cost[] Push(Cost[] recent, Cost cost)
    {
        Cost[] next = [.. recent, cost];
        return next.Length > RecentLimit ? next[^RecentLimit..] : next;
    }

    private static readonly UsageGrouping[] Groupings =
    [
        UsageGrouping.Origin,
        UsageGrouping.App,
        UsageGrouping.Synapse,
        UsageGrouping.Neuron,
        UsageGrouping.Kind,
    ];
}