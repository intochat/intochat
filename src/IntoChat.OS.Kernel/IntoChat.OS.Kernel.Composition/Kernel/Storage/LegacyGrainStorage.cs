using System.Globalization;
using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Framework;
using Orleans.Serialization;
using Orleans.Storage;

namespace IntoChat.OS.Kernel.Composition;

// Transitional (06-02, R4-12, deleted in 06-08): an unmigrated Framework grain's state, kept in the Orleans binary format it
// always had, written as an opaque record into the runtime BrainLog through IBrainJournalWriter. The signals its Save attached
// ride in the same entry, so they are durable before any reader sees them, as with the os BrainLog before.
internal sealed class LegacyGrainStorage(IBrainJournalWriter journal, Serializer serializer, string? fixedBrainScope = null) : IGrainStorage
{
    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        ArgumentNullException.ThrowIfNull(grainState);
        var stored = await journal.ReadState(Brain(grainId), grainId.ToString(), stateName, CancellationToken.None);
        if (stored is not null) { grainState.State = serializer.Deserialize<T>(stored.Record.ToArray())!; }
        grainState.RecordExists = stored is not null;
        grainState.ETag = stored?.ETag.ToString(CultureInfo.InvariantCulture);
    }

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        ArgumentNullException.ThrowIfNull(grainState);
        var sequence = await Append(stateName, grainId, grainState.ETag, JournalStateChange.Write, serializer.SerializeToArray(grainState.State), typeof(T));
        grainState.ETag = sequence.ToString(CultureInfo.InvariantCulture);
        grainState.RecordExists = true;
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        ArgumentNullException.ThrowIfNull(grainState);
        var fresh = Activator.CreateInstance<T>();
        await Append(stateName, grainId, grainState.ETag, JournalStateChange.Clear, [], typeof(T));
        grainState.State = fresh;
        grainState.RecordExists = false;
        grainState.ETag = null;
    }

    // A neuron's state lives in its brain's log; a key outside every brain is refused, as the os BrainLog did.
    private string Brain(GrainId grainId)
    {
        if (fixedBrainScope is not null) { return fixedBrainScope; }
        var brain = LegacyPublishers.BrainOf(grainId);
        return BrainScope.IsValidId(brain) ? brain : throw new ArgumentException($"'{grainId}' is not in a brain.", nameof(grainId));
    }

    private async Task<long> Append(string stateName, GrainId grainId, string? etag, JournalStateChange change, byte[] record, Type stateType)
    {
        var pending = SignalJournal.TakePending(grainId);
        long? expected = etag is null ? null : long.TryParse(etag, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
        try
        {
            JournalSignalWrite[] signals = [.. pending.Select(signal => LegacySignalCodec.Encode(signal, serializer))];
            var result = await journal.Append(Brain(grainId), new JournalStateWrite(grainId.ToString(), stateName, change, expected, record, stateType.FullName),
                LegacyPublishers.Of(grainId), signals, CancellationToken.None);
            if (pending.Length > 0) { SignalJournal.Appended(grainId, result.LastSignal); }
            return result.Sequence;
        }
        // Refused before anything committed: the Save behind this write gets its signals back.
        catch (Exception error) when (error is InconsistentStateException or ArgumentException or JournalPayloadException or NotSupportedException or InvalidOperationException)
        { SignalJournal.Restore(grainId, pending); throw; }
    }
}
