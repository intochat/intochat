using System.Globalization;
using DigitalBrain.Framework.Parts;
using Orleans.Serialization;
using Orleans.Storage;

namespace DigitalBrain.Platform.Parts;

// The cluster grain storage on a machine (Q27): every neuron record is an entry in its part's log, and the
// ETag is the seq of the entry that last wrote it.
internal sealed class PartGrainStorage(PartLogs logs, Serializer serializer) : IGrainStorage
{
    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var log = await logs.For(PartId.Of(grainId.Key.ToString()!));
        if (log.Read(grainId.ToString(), stateName) is { } stored)
        {
            grainState.State = serializer.Deserialize<T>(stored.Record)!;
            (grainState.ETag, grainState.RecordExists) = (stored.ETagSeq.ToString(CultureInfo.InvariantCulture), true);
        }
        // No record: the state keeps the instance the runtime created, as Orleans' own providers leave it
        // (a record type such as the brain's State has no parameterless constructor to make one here).
        else { (grainState.ETag, grainState.RecordExists) = (null, false); }
    }

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var log = await Checked(stateName, grainId, grainState);
        // Task 14 replaces [] with the signals pending for this write, so they commit in the same entry.
        var seq = await log.Append(grainId.ToString(), stateName, RecordChange.Write, serializer.SerializeToArray(grainState.State), []);
        (grainState.ETag, grainState.RecordExists) = (seq.ToString(CultureInfo.InvariantCulture), true);
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var log = await Checked(stateName, grainId, grainState);
        await log.Append(grainId.ToString(), stateName, RecordChange.Clear, [], []);
        (grainState.ETag, grainState.RecordExists) = (null, false);
    }

    private async Task<PartLog> Checked<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var log = await logs.For(PartId.Of(grainId.Key.ToString()!));
        var stored = log.Read(grainId.ToString(), stateName)?.ETagSeq.ToString(CultureInfo.InvariantCulture);
        if (grainState.ETag != stored) { throw new InconsistentStateException($"ETag {grainState.ETag} of {grainId}/{stateName} is stale.", stored, grainState.ETag); }
        return log;
    }
}
