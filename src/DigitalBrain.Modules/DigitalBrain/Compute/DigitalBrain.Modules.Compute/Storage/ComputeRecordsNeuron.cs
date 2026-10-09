using DigitalBrain;
using DigitalBrain.Compute.Usage;
using DigitalBrain.Framework;
using Orleans.Runtime;

namespace DigitalBrain.Compute.Storage;

// A non-reentrant writer serializes deduplication; records live directly in grain state.
// Meter shards are billing facts and never evict (they are bounded by sharding per month);
// usage-log grains, recognized by their key, keep only the newest Cap rows.
[GrainType("compute.records")]
internal sealed class ComputeRecordsNeuron(
    [PersistentState("records", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<ComputeRecordsState> state)
    : Neuron, IComputeRecords
{
    internal const int Cap = 10_000;
    private const string UsageSegment = "usage";

    internal static string UsageKey(string account, string brainId) => $"{brainId}/{UsageSegment}/{UsagePaging.Scope(account, brainId)}";

    private static bool IsUsageKey(string key) => key.Split('/') is [_, UsageSegment, _];

    public async Task<int> Put(ComputeStoredRecord[] records, bool overwrite)
    {
        if (records.Length > 1000) { throw new ArgumentException("At most 1000 records per batch."); }
        var byId = state.State.Records.ToDictionary(record => record.Id, StringComparer.Ordinal);
        var changedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(record.Id);
            if (record.Id.Length > 4096 || record.Payload.Length > 1024 * 1024 || record.SortKey.Length > 256)
            { throw new ArgumentException("Compute record exceeds its size limit."); }
            var old = byId.GetValueOrDefault(record.Id);
            if (old is not null && (!overwrite || record.Revision < old.Revision)) { continue; }
            var next = old is null ? record : record with { SortKey = old.SortKey };
            if (old == next) { continue; }
            byId[record.Id] = next;
            changedIds.Add(record.Id);
        }
        var kept = byId.Values.OrderBy(record => record.SortKey, StringComparer.Ordinal).ToArray();
        if (kept.Length > Cap && IsUsageKey(this.GetPrimaryKeyString())) { kept = kept[^Cap..]; }
        // A record evicted in the same call was never recorded, so it is not counted.
        var changed = kept.Count(record => changedIds.Contains(record.Id));
        if (changed == 0) { return 0; }
        var previous = state.State;
        state.State = new ComputeRecordsState { Records = kept };
        try { await state.WriteStateAsync(); }
        catch { state.State = previous; throw; }
        return changed;
    }

    public Task<ComputeStoredRecord[]> Read() => Task.FromResult(state.State.Records);

    public Task<ComputeRecordPage> Page(string? before, int limit)
    {
        if (limit is < 1 or > 100) { throw new ArgumentException("Invalid page size."); }
        var slice = state.State.Records.Reverse()
            .Where(record => before is null || StringComparer.Ordinal.Compare(record.SortKey, before) < 0)
            .Take(limit + 1).ToArray();
        var rows = slice.Take(limit).ToArray();
        return Task.FromResult(new ComputeRecordPage(rows, slice.Length > limit ? rows[^1].SortKey : null));
    }
}
