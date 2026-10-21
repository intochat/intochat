using System.Globalization;
using IntoChat.OS.Framework;
using Orleans.Serialization;
using Orleans.Storage;

namespace IntoChat.OS.Authority.Storage;

internal sealed class BrainGrainStorage(BrainLogs logs, Serializer serializer, string? fixedBrainScope = null) : IGrainStorage
{
    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var record = await (await Log(grainId)).Read(grainId.ToString(), stateName);
        if (record is not null) { grainState.State = serializer.Deserialize<T>(record.Record)!; }
        grainState.RecordExists = record is not null;
        grainState.ETag = record?.ETag.ToString(CultureInfo.InvariantCulture);
    }

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var seq = await Append(stateName, grainId, grainState.ETag, BrainRecordChange.Write, serializer.SerializeToArray(grainState.State));
        grainState.ETag = seq.ToString(CultureInfo.InvariantCulture);
        grainState.RecordExists = true;
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var fresh = Activator.CreateInstance<T>();
        await Append(stateName, grainId, grainState.ETag, BrainRecordChange.Clear, []);
        grainState.State = fresh;
        grainState.RecordExists = false;
        grainState.ETag = null;
    }

    private Task<BrainLog> Log(GrainId grainId) => logs.For(fixedBrainScope ?? BrainKey.BrainOf(grainId.Key.ToString()!));

    private async Task<long> Append(string stateName, GrainId grainId, string? etag, BrainRecordChange change, byte[] record)
    {
        var log = await Log(grainId);
        var pending = SignalJournal.TakePending(grainId);
        long? expected = etag is null ? null : long.TryParse(etag, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
        try
        {
            var (sequence, lastSignal) = await log.AppendEntry(grainId.ToString(), stateName, expected, change, record, pending, CancellationToken.None);
            if (pending.Length > 0) { SignalJournal.Appended(grainId, lastSignal); }
            return sequence;
        }
        catch (Exception error) when (error is InconsistentStateException or ArgumentException)
        { SignalJournal.Restore(grainId, pending); throw; }
    }
}
