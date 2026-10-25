namespace IntoChat.OS.Modules.Google.Gmail;

// A mailbox-change wake-up: reconcile current state; this does not identify a fetched message.
[GenerateSerializer, Alias("gmail.mail-received")]
public sealed record MailReceived(
    [property: Id(0)] string EmailAddress,
    [property: Id(1)] string HistoryId) : Signal;
