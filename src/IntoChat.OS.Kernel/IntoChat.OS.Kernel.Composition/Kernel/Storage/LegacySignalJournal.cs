using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Framework;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;

namespace IntoChat.OS.Kernel.Composition;

// Transitional (06-02, R4-12, deleted in 06-08): the os ISignalJournal over the runtime BrainLog. Legacy publishers append
// through IBrainJournalWriter and every os reader (observers, the brain signal reader, the signals endpoints) reads back the
// os Signals of the same log. Brain reads number by brain position; publisher reads by the publisher's own sequence, the
// number a legacy Save reports through SignalJournal.Appended. Entries the os cannot decode (plain neurons' records) are skipped.
internal sealed partial class LegacySignalJournal(IBrainJournalWriter journal, Serializer serializer, ILogger<LegacySignalJournal> logger) : ISignalJournal
{
    private const int Page = global::DigitalBrain.SignalPage.MaxLimit;

    public async Task<long> Append(GrainId publisher, Signal[] signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        JournalSignalWrite[] encoded = [.. signals.Select(signal => LegacySignalCodec.Encode(signal, serializer))];
        return (await journal.Append(LegacyPublishers.BrainOf(publisher), null, LegacyPublishers.Of(publisher), encoded, CancellationToken.None)).LastSignal;
    }

    public async Task<JournaledSignal[]> ReadSince(string brainId, long since, int limit)
    {
        if (limit <= 0) { return []; }
        var read = new List<JournaledSignal>();
        var cursor = Math.Max(0, since);
        while (read.Count < limit)
        {
            var page = await journal.ReadSince(brainId, cursor, Page, CancellationToken.None);
            if (page.Signals.Count == 0) { break; }
            foreach (var entry in page.Signals)
            {
                cursor = entry.Position;
                if (Decode(entry) is { } signal) { read.Add(new JournaledSignal(entry.Position, signal)); }
                if (read.Count == limit) { break; }
            }
        }
        return [.. read];
    }

    public async Task<PartSignals?> SignalsOf(GrainId publisher, long since)
    {
        var brain = LegacyPublishers.BrainOf(publisher);
        var name = LegacyPublishers.Of(publisher);
        var read = new List<JournaledSignal>();
        var cursor = Math.Max(0, since);
        JournalSignalPage page;
        do
        {
            page = await journal.ReadSince(brain, name, cursor, Page, CancellationToken.None);
            foreach (var entry in page.Signals)
            {
                cursor = entry.Sequence;
                if (Decode(entry) is { } signal) { read.Add(new JournaledSignal(entry.Sequence, signal)); }
            }
        }
        while (page.Signals.Count > 0 && cursor < page.Last);
        return new PartSignals(page.Floor, page.Last, [.. read]);
    }

    private Signal? Decode(JournalSignalEntry entry)
    {
        try
        {
            var signal = LegacySignalCodec.Decode(entry.Json, serializer);
            if (signal is null) { LogNotAnOsSignal(entry.Type, entry.Publisher, entry.Position); }
            return signal;
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or FormatException or Orleans.Serialization.CodecNotFoundException or InvalidOperationException)
        {
            LogUndecodable(error, entry.Type, entry.Publisher, entry.Position);
            return null;
        }
    }

    [LoggerMessage(EventId = 6201, Level = LogLevel.Debug, Message = "Signal type {Type} of {Publisher} at {Position} is not an os signal; os readers skip it.")]
    private partial void LogNotAnOsSignal(string type, string publisher, long position);

    [LoggerMessage(EventId = 6202, Level = LogLevel.Warning, Message = "Signal type {Type} of {Publisher} at {Position} could not be decoded; os readers skip it.")]
    private partial void LogUndecodable(Exception error, string type, string publisher, long position);
}
