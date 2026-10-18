namespace IntoChat.OS;

// A refusal word: this machine no longer holds the part it was writing; nothing after this is durable.
[GenerateSerializer, Alias("framework.lease-lost")]
public sealed class LeaseLostException(string message) : Exception(message);
