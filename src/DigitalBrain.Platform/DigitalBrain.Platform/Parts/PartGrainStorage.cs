using DigitalBrain.Framework;
using System.Globalization;
using DigitalBrain.Framework.Parts;
using Orleans.Serialization;
using Orleans.Storage;

namespace DigitalBrain.Platform.Parts;

// The cluster grain storage on a machine (Q27): every neuron record is an entry in its part's log, and the
// ETag is the seq of the entry that last wrote it.
// R101: on a machine of a trust cluster, a grain activated here (through the gateway) for a part held by another
// machine reads nothing: it is a stand-in that only forwards its calls (PartIncomingCallFilter).
internal sealed class PartGrainStorage(PartLogs logs, Serializer serializer, PartRouter? router = null) : IGrainStorage
{
    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        if (router is not null && await router.Resolve(PartId.Of(grainId.Key.ToString()!)) is PartRoute.Remote)
        {
            router.MarkStandIn(grainId);
            (grainState.ETag, grainState.RecordExists) = (null, false);
            return;
        }
        // A new activation holding its part replaces any stand-in this grain had before.
        router?.ForgetStandIn(grainId);
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
        RefuseStandIn(grainId);
        var log = await logs.For(PartId.Of(grainId.Key.ToString()!));
        // The signals of the Save behind this write commit in the same entry, so neither is durable without the other.
        var seq = await log.AppendIfCurrent(grainId.ToString(), stateName, Expected(grainState), RecordChange.Write, serializer.SerializeToArray(grainState.State),
            SignalJournal.TakePending(grainId));
        (grainState.ETag, grainState.RecordExists) = (seq.ToString(CultureInfo.InvariantCulture), true);
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        RefuseStandIn(grainId);
        var log = await logs.For(PartId.Of(grainId.Key.ToString()!));
        await log.AppendIfCurrent(grainId.ToString(), stateName, Expected(grainState), RecordChange.Clear, [], SignalJournal.TakePending(grainId));
        // A cleared activation reads as a fresh one, as Orleans' own providers leave it; a record type without a
        // parameterless constructor keeps its instance, as ReadStateAsync does for a missing record.
        if (typeof(T).IsValueType || typeof(T).GetConstructor(Type.EmptyTypes) is not null) { grainState.State = Activator.CreateInstance<T>(); }
        (grainState.ETag, grainState.RecordExists) = (null, false);
    }

    // A stand-in read no state; a write through it would open the part on a machine that does not hold it.
    private void RefuseStandIn(GrainId grainId)
    {
        if (router?.IsStandIn(grainId) == true)
        { throw new ServiceUnavailableException($"{grainId} is a stand-in for a part held by another machine; it writes nothing here."); }
    }

    private static long? Expected<T>(IGrainState<T> grainState) =>
        grainState.ETag is null ? null : long.TryParse(grainState.ETag, CultureInfo.InvariantCulture, out var seq) ? seq : -1;
}
