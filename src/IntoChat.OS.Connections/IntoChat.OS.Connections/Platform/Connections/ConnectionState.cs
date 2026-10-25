using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Connections.Platform.Connections;

[GenerateSerializer, Alias("sealed-secret")]
public sealed record SealedSecret(
    [property: Id(0)] string Name,
    [property: Id(1)] string KeyId,
    [property: Id(2)] string Ciphertext);

// Mutable on purpose: the base mutates the generic TState in place and writes it.
[GenerateSerializer, Alias("connection.state")]
public class ConnectionState
{
    [Id(0)] public string AuthMethodId { get; set; } = "";
    [Id(1)] public ConnectionStatus Status { get; set; } = ConnectionStatus.Disconnected;
    [Id(2)] public SealedSecret[] Secrets { get; set; } = [];
    [Id(3)] public string? Account { get; set; }
    [Id(4)] public string Scopes { get; set; } = "";
    [Id(5)] public DateTimeOffset? ExpiresAt { get; set; }
    [Id(6)] public DateTimeOffset? LastProbedAt { get; set; }
    [Id(7)] public string? Detail { get; set; }
    // R146: "{brain}/{app}" of every app of another brain this connection is shared into.
    [Id(8)] public string[] SharedInto { get; set; } = [];
}
