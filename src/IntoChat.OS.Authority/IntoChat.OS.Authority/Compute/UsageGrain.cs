using System.Globalization;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Compute;
using IntoChat.OS.Contracts.Compute;
using Orleans.Concurrency;

namespace IntoChat.OS.Authority.Compute;

// Per brain. Every cost lands in the Recent ring buffer and rolls into one daily bucket per
// grouping, so Group aggregates buckets instead of scanning the ring. Every metered call of the brain
// lands here, so costs come in bursts: reentrant, each Add applies its cost at once and shares the write
// that is out (CoalescedWrites), and the reads answer from memory without waiting behind a write.
[GrainType("usage")]
[Reentrant]
internal sealed class UsageGrain(
    [PersistentState("usage", IntoChatOSNames.PlatformGrainStorage)] IPersistentState<UsageState> store)
    : Grain, IUsageGrain
{
    // The ring holds what GET /compute/recent serves at most; the totals come from the daily buckets.
    internal const int RecentLimit = ComputeEndpoints.MaximumTake;
    private const int DailyRetentionDays = 90;
    private CoalescedWrites? _writes;

    private CoalescedWrites Writes => _writes ??= new(store.WriteStateAsync);

    public async Task Apply(UsageProjection projection)
    {
        if (projection.Sequence <= store.State.AppliedSequence) { await Writes.Save(); return; }
        if (projection.Sequence != store.State.AppliedSequence + 1) { throw new InvalidOperationException("Compute projection has a sequence gap."); }
        var cost = projection.Cost;
        var previous = projection.Previous;
        store.State.Recent = Push([.. store.State.Recent.Where(value => value.TraceId != cost.TraceId || value.CostId != cost.CostId)], cost);
        var day = cost.At.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var by in Groupings)
        {
            var key = KeyFor(cost, by, projection.Origin);
            var index = Array.FindIndex(store.State.Daily, value => value.Day == day && value.By == by && value.Key == key);
            var bucket = index < 0 ? new UsageBucket(day, by, key, 0, 0) : store.State.Daily[index];
            var next = bucket with
            {
                Micros = checked(bucket.Micros + cost.Micros - (previous?.Micros ?? 0)),
                Count = checked(bucket.Count + (previous is null ? 1 : 0)),
                KnownListPriceMicros = checked((bucket.KnownListPriceMicros ?? 0) + Known(cost) - Known(previous)),
                UnpricedCount = (bucket.UnpricedCount ?? 0) + Unpriced(cost) - Unpriced(previous),
                PendingCount = (bucket.PendingCount ?? 0) + Pending(cost) - Pending(previous),
                UnknownCount = (bucket.UnknownCount ?? bucket.Count) + Unknown(cost) - Unknown(previous),
            };
            if (index < 0) { store.State.Daily = [.. store.State.Daily, next]; }
            else { store.State.Daily[index] = next; }
        }
        store.State.AppliedSequence = projection.Sequence;
        Prune();
        await Writes.Save();
    }

    private static long Known(Cost? cost) => cost?.ChargePolicy == ExternalChargePolicy.Informational ? 0 : cost?.ListPriceMicros ?? 0;
    private static long Unpriced(Cost? cost) => cost?.PricingStatus == PricingStatus.Unpriced ? 1 : 0;
    private static long Pending(Cost? cost) => cost is not null && TraceJournalGrain.IsPending(cost) ? 1 : 0;
    private static long Unknown(Cost? cost) => cost is not null && cost.PricingStatus is null or PricingStatus.Unknown ? 1 : 0;

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
        var totals = new Dictionary<string, CostRow>();
        foreach (var bucket in store.State.Daily)
        {
            if (bucket.By != by || string.CompareOrdinal(bucket.Day, sinceDay) < 0) { continue; }
            totals.TryGetValue(bucket.Key, out var existing);
            totals[bucket.Key] = new(bucket.Key, checked((existing?.Micros ?? 0) + bucket.Micros), checked((existing?.Count ?? 0) + bucket.Count))
            {
                KnownListPriceMicros = (existing?.KnownListPriceMicros ?? 0) + (bucket.KnownListPriceMicros ?? 0),
                UnpricedCount = (existing?.UnpricedCount ?? 0) + (bucket.UnpricedCount ?? 0),
                PendingCount = (existing?.PendingCount ?? 0) + (bucket.PendingCount ?? 0),
                UnknownCount = (existing?.UnknownCount ?? 0) + (bucket.UnknownCount ?? bucket.Count),
            };
        }
        var rows = totals
            .Select(pair => pair.Value)
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
