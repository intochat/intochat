using DigitalBrain.Capabilities.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Connections.Platform.Connections.Signals;

[DigitalBrain.Signal("connection.connected")]
public sealed record ConnectionConnected(string ConnectorId, string ConnectionId, string AuthMethodId, string? Account) : DigitalBrain.Signal;

[DigitalBrain.Signal("connection.expired")]
public sealed record ConnectionExpired(string ConnectorId, string ConnectionId) : DigitalBrain.Signal;

[DigitalBrain.Signal("connection.failing")]
public sealed record ConnectionFailing(string ConnectorId, string ConnectionId, string Detail) : DigitalBrain.Signal;

[DigitalBrain.Signal("connection.disconnected")]
public sealed record ConnectionDisconnected(string ConnectorId, string ConnectionId) : DigitalBrain.Signal;

// A connection's state changed without a status change (a sharing recorded or withdrawn, a module's own bookkeeping): a plain
// neuron saves only with a signal (06-08).
[DigitalBrain.Signal("connection.updated")]
public sealed record ConnectionUpdated(string ConnectorId, string ConnectionId) : DigitalBrain.Signal;
