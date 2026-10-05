using DigitalBrain.Framework.Platform.Integrations.Accounts;

namespace DigitalBrain.Platform.Integrations.Accounts;

[GenerateSerializer, Alias("connections.state")] // alias predates the integrations rename; persisted, do not touch
internal sealed record ConnectionsState
{
    [Id(0)] public Dictionary<string, Connection> Connections { get; init; } = new(StringComparer.Ordinal);
}
