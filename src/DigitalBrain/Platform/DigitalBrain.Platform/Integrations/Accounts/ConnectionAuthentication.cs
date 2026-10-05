using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Framework.Platform.Integrations.Accounts;

namespace DigitalBrain.Platform.Integrations.Accounts;

internal static class ConnectionAuthentication
{
    internal static Connection Normalize(Connection connection, IReadOnlyList<ConnectorDefinition> connectors)
    {
        var methods = connectors.FirstOrDefault(c => c.Id == connection.IntegrationId)?.AuthMethods ?? [];
        if (methods.Contains(connection.AuthMethodId, StringComparer.Ordinal)) { return connection; }
        return string.IsNullOrEmpty(connection.AuthMethodId) && methods.Length == 1
            ? connection with { AuthMethodId = methods[0] }
            : connection with { Status = ConnectionStatus.Disconnected };
    }
}
