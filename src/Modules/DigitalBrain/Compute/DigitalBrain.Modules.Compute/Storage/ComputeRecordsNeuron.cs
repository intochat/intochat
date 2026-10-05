using DigitalBrain;
using DigitalBrain.Framework;
using Orleans.Runtime;

namespace DigitalBrain.Compute.Storage;

// A non-reentrant writer serializes deduplication; records live directly in grain state.
// Every log this grain backs is bounded: meters shard per month, usage evicts past Cap.
[GrainType("compute.records")]
internal sealed class ComputeRecordsNeuron(
    [PersistentState("records", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<ComputeRecordsState> state)
    : Neuron, IComputeRecords
{
    internal const int Cap = 10_000;

    public async Task<int> Put(ComputeStoredRecord[] records, bool overwrite)
    {
        if (records.Length > 1000) { throw new ArgumentException("At most 1000 records per batch."); }
        var byId = state.State.Records.ToDictionary(record => record.Id, StringComparer.Ordinal);
        var changed = 0;
        foreach (var record in records)
        {
            var old = byId.GetValueOrDefault(record.Id);
            if (old is not null && (!overwrite || record.Revision < old.Revision)) { continue; }
            var next = old is null ? record : record with { SortKey = old.SortKey };
            if (old == next) { continue; }
            byId[record.Id] = next;
            changed++;
        }
        if (changed == 0) { return 0; }
        var kept = byId.Values.OrderBy(record => record.SortKey, StringComparer.Ordinal).ToArray();
        if (kept.Length > Cap) { kept = kept[^Cap..]; }
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
