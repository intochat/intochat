using System.Globalization;
using Orleans.Runtime;
using Orleans.Storage;

namespace DigitalBrain.Runtime.Orleans;

/// <summary>
/// The <c>cluster</c> grain store: every grain state of a brain lives in that brain's journal, encoded as JSON through
/// <see cref="JournalPayloads"/> (06 D-14). Orleans-internal stores (PubSubStore, reminders) never come here.
/// </summary>
internal sealed class BrainGrainStorage(BrainLogs logs, string? fixedBrainScope = null) : IGrainStorage
{
    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        ArgumentNullException.ThrowIfNull(grainState);
        var record = await (await Log(grainId)).Read(grainId.ToString(), stateName);
        if (record is not null) { grainState.State = JournalPayloads.DecodeState<T>(record.Record)!; }
        grainState.RecordExists = record is not null;
        grainState.ETag = record?.ETag.ToString(CultureInfo.InvariantCulture);
    }

    public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        => WriteStateAsync(stateName, grainId, grainState, "", []);

    /// <summary>Writes the state and the signals its publisher emitted with it as one durable entry; returns the publisher's last sequence.</summary>
    internal async Task<long> WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState, string publisher, IReadOnlyList<Signal> signals)
    {
        ArgumentNullException.ThrowIfNull(grainState);
        var (seq, last) = await Append(stateName, grainId, grainState.ETag, BrainRecordChange.Write, JournalPayloads.EncodeState(grainState.State), publisher, signals);
        grainState.ETag = seq.ToString(CultureInfo.InvariantCulture);
        grainState.RecordExists = true;
        return last;
    }

    /// <summary>Appends the <paramref name="signals"/> of <paramref name="publisher"/> as one entry with no state change; returns its last sequence.</summary>
    internal async Task<long> AppendSignalsAsync(string stateName, GrainId grainId, string publisher, IReadOnlyList<Signal> signals)
        => (await Append(stateName, grainId, null, BrainRecordChange.None, [], publisher, signals)).Last;

    /// <summary>The journal of the brain <paramref name="grainId"/> belongs to.</summary>
    internal Task<BrainLog> LogOf(GrainId grainId) => Log(grainId);

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        ArgumentNullException.ThrowIfNull(grainState);
        var fresh = Activator.CreateInstance<T>();
        await Append(stateName, grainId, grainState.ETag, BrainRecordChange.Clear, [], "", []);
        grainState.State = fresh;
        grainState.RecordExists = false;
        grainState.ETag = null;
    }

    private Task<BrainLog> Log(GrainId grainId) => logs.For(fixedBrainScope ?? BrainStorageKeys.BrainOf(grainId.Key.ToString()!));

    private async Task<(long Sequence, long Last)> Append(string stateName, GrainId grainId, string? etag, BrainRecordChange change, byte[] record,
        string publisher, IReadOnlyList<Signal> signals)
    {
        var log = await Log(grainId);
        long? expected = etag is null ? null : long.TryParse(etag, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
        return await log.AppendEntry(grainId.ToString(), stateName, expected, change, record, publisher, signals, CancellationToken.None);
    }
}

/// <summary>Grain storage that keeps every state of one scope in that scope's brain journal.</summary>
public static class PlatformGrainStorage
{
    /// <summary>A grain store over the journal of <paramref name="scope"/>; needs <c>AddBrainStorage</c>.</summary>
    public static IGrainStorage ForScope(IServiceProvider services, string scope)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return new BrainGrainStorage((BrainLogs)(services.GetService(typeof(BrainLogs))
            ?? throw new InvalidOperationException("PlatformGrainStorage needs AddBrainStorage.")), scope);
    }
}
