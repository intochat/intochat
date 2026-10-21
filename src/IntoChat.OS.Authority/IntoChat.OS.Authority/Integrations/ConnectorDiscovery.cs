using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Platform.Integrations;

namespace IntoChat.OS.Authority.Integrations;

internal static class ConnectorDiscovery
{
    internal static ConnectorDefinition[] Collect(ModuleInventory inventory, IReadOnlyList<IntegrationSchema> schemas)
    {
        var connectors = inventory.Types.SelectMany(ConnectorDefinition.DeclaredBy).ToArray();
        Validate(connectors, schemas);
        return connectors;
    }

    internal static void Validate(ConnectorDefinition[] connectors, IReadOnlyList<IntegrationSchema> schemas)
    {
        if (connectors.GroupBy(c => c.Id, StringComparer.Ordinal).Any(g => g.Count() > 1))
        { throw new InvalidOperationException("Connector identifiers must be unique."); }
        foreach (var connector in connectors)
        {
            _ = IntegrationSchema.For(connector.Id, connector.DisplayName);
            if (connector.AuthMethods.Length == 0 || connector.AuthMethods.Any(string.IsNullOrWhiteSpace)
                || connector.AuthMethods.Distinct(StringComparer.Ordinal).Count() != connector.AuthMethods.Length)
            { throw new InvalidOperationException($"Connector '{connector.Id}' must declare distinct authentication methods."); }
            foreach (var method in connector.OAuthMethods)
            {
                if (!connector.AuthMethods.Contains(method.Id, StringComparer.Ordinal))
                { throw new InvalidOperationException($"Connector '{connector.Id}' declares OAuth method '{method.Id}' outside its authentication methods."); }
                if (!connector.RequiredIntegrations.Concat(connector.OptionalIntegrations).Contains(method.IntegrationId, StringComparer.Ordinal))
                { throw new InvalidOperationException($"Connector '{connector.Id}' OAuth method '{method.Id}' names an undeclared Integration '{method.IntegrationId}'."); }
            }
            if (connector.Capabilities.Contains(IntoChat.OS.Storage.StorageCapability.Kind, StringComparer.Ordinal)
                && !connector.Neurons.Any(typeof(IntoChat.OS.Storage.IStorage).IsAssignableFrom))
            { throw new InvalidOperationException($"Connector '{connector.Id}' declares storage but lists no IStorage neuron."); }
            foreach (var id in connector.RequiredIntegrations.Concat(connector.OptionalIntegrations))
            {
                if (!schemas.Any(s => s.Id == id))
                { throw new InvalidOperationException($"Connector '{connector.Id}' requires undeclared Integration '{id}'."); }
            }
        }
    }
}
