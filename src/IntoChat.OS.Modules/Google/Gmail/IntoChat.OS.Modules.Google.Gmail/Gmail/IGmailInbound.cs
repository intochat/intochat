namespace IntoChat.OS.Modules.Google.Gmail;

[Alias("gmail.inbound"), Orleans.Metadata.DefaultGrainType("gmail")]
internal interface IGmailInbound : IGrainWithStringKey
{
    Task<GmailPushAcceptance> AcceptWatchPush(GmailWatchPush push);
}

internal enum GmailPushAcceptance { Accepted, Duplicate, Ignored, Invalid, Retryable }
