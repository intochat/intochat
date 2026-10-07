using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;

namespace DigitalBrain.Google.Gmail;

[PlatformOnly, Alias("gmail.inbound"), Orleans.Metadata.DefaultGrainType("gmail")]
internal interface IGmailInbound : INeuron
{
    Task AcceptWatchPush(GmailWatchPush push);
}
