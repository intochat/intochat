namespace IntoChat.OS.Google.Gmail;

[GenerateSerializer, Alias("db.gmail.unavailable")]
public sealed class GmailUnavailableException(string message) : InvalidOperationException(message);
