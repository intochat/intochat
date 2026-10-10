using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;

namespace DigitalBrain.Platform.Parts;

// A signal published without a state write is its own entry in the publisher's part log.
internal sealed class PartSignalJournal(PartLogs logs) : ISignalJournal
{
    public async Task Append(GrainId publisher, Signal[] signals)
    {
        var log = await logs.For(PartId.Of(publisher.Key.ToString()!));
        await log.Append(publisher.ToString(), "", RecordChange.None, [], signals);
    }
}
