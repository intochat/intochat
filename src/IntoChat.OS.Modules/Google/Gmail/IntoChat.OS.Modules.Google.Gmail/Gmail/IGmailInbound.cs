using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Contracts;

namespace IntoChat.OS.Google.Gmail;

[Alias("gmail.inbound"), Orleans.Metadata.DefaultGrainType("gmail")]
internal interface IGmailInbound : IGrainWithStringKey
{
    Task AcceptWatchPush(GmailWatchPush push);
}
