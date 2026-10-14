using DigitalBrain.Framework;

namespace DigitalBrain.Platform.Storage;

internal sealed class BrainSignalJournal(BrainLogs logs) : ISignalJournal
{
    public async Task Append(GrainId publisher, Signal[] signals)
    {
        var log = await logs.For(BrainKey.BrainOf(publisher.Key.ToString()!));
        await log.Append(publisher.ToString(), "", null, BrainRecordChange.None, [], signals, CancellationToken.None);
    }

    public async Task<JournaledSignal[]> ReadSince(string brainId, long since, int limit)
        => await (await logs.For(brainId)).ReadSignals(since, limit, null, CancellationToken.None);

    public async Task<PartSignals?> SignalsOf(GrainId publisher, long since)
    {
        var log = await logs.For(BrainKey.BrainOf(publisher.Key.ToString()!));
        return await log.PublisherSignals(publisher.ToString(), since, CancellationToken.None);
    }
}
