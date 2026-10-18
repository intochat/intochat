namespace IntoChat.OS.Framework.Platform.Connections.Signals;

[GenerateSerializer, Alias("connection.connected")]
public sealed record ConnectionConnected([property: Id(0)] string ConnectorId, [property: Id(1)] string ConnectionId, [property: Id(2)] string AuthMethodId, [property: Id(3)] string? Account) : Signal;

[GenerateSerializer, Alias("connection.expired")]
public sealed record ConnectionExpired([property: Id(0)] string ConnectorId, [property: Id(1)] string ConnectionId) : Signal;

[GenerateSerializer, Alias("connection.failing")]
public sealed record ConnectionFailing([property: Id(0)] string ConnectorId, [property: Id(1)] string ConnectionId, [property: Id(2)] string Detail) : Signal;

[GenerateSerializer, Alias("connection.disconnected")]
public sealed record ConnectionDisconnected([property: Id(0)] string ConnectorId, [property: Id(1)] string ConnectionId) : Signal;
