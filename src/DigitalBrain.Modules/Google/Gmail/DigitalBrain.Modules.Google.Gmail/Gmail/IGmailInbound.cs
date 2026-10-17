using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;

namespace DigitalBrain.Google.Gmail;

[Alias("gmail.inbound"), Orleans.Metadata.DefaultGrainType("gmail")]
internal interface IGmailInbound : IGrainWithStringKey
{
    Task AcceptWatchPush(GmailWatchPush push);
}
