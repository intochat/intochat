using IntoChat.OS;

namespace IntoChat.OS.Google.Gmail;

[Alias("gmail")]
[Orleans.Metadata.DefaultGrainType("gmail")]
public interface IGmail : INeuron
{
}

[GenerateSerializer, Alias("gmail.watch-push")]
public sealed record GmailWatchPush(
    [property: Id(0)] string HistoryId,
    [property: Id(1)] string EmailAddress);
