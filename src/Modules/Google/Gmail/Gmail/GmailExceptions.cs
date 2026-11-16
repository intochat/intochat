namespace IntoChat.OS.Modules.Google.Gmail;

[GenerateSerializer, Alias("db.gmail.unavailable")]
internal sealed class GmailUnavailableException(string message) : InvalidOperationException(message);
