using DigitalBrain.Framework.Platform.Integrations.Accounts;

namespace DigitalBrain.Platform.Integrations.Accounts;

[GenerateSerializer, Alias("connections.status-changed")]
public sealed record ConnectionStatusChanged(string ConnectionId, string IntegrationId, ConnectionStatus Status) : Signal;

[GenerateSerializer, Alias("connections.disconnected")]
public sealed record ConnectionDisconnected(string ConnectionId) : Signal;
