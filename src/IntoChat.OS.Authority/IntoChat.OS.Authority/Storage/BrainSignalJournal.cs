using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Authority.Storage;

internal sealed class BrainSignalJournal(BrainLogs logs) : ISignalJournal
{
    public async Task<long> Append(GrainId publisher, Signal[] signals)
    {
        var log = await logs.For(BrainKey.BrainOf(publisher.Key.ToString()!));
        return (await log.AppendEntry(publisher.ToString(), "", null, BrainRecordChange.None, [], signals, CancellationToken.None)).LastSignal;
    }

    public async Task<JournaledSignal[]> ReadSince(string brainId, long since, int limit)
        => await (await logs.For(brainId)).ReadSignals(since, limit, null, CancellationToken.None);

    public async Task<PartSignals?> SignalsOf(GrainId publisher, long since)
    {
        var log = await logs.For(BrainKey.BrainOf(publisher.Key.ToString()!));
        return await log.PublisherSignals(publisher.ToString(), since, CancellationToken.None);
    }
}
