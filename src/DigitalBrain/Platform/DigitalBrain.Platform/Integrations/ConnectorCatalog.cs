using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Platform.Integrations;

internal sealed record ConnectorCatalogEntry(string Id, string DisplayName, string[] AuthMethods, string[] RequiredIntegrations, string[] OptionalIntegrations, string[] Neurons, string[] Signals, string ExternalSystem);

internal sealed class ConnectorCatalog(IReadOnlyList<ConnectorDefinition> connectors, IGrainFactory grains, ILogger<ConnectorCatalog> logger)
{
    public async Task<ConnectorCatalogEntry[]> ListAsync(CancellationToken cancellationToken)
    {
        var result = new List<ConnectorCatalogEntry>();
        foreach (var connector in connectors)
        {
            var ready = true;
            foreach (var dependency in connector.RequiredIntegrations)
            {
                var snapshot = await grains.GetGrain<IIntegration>(IntegrationVault.GrainKeyPrefix + dependency).Read().WaitAsync(cancellationToken);
                if (snapshot.Status == IntegrationStatus.Ready) { continue; }
                ready = false;
                logger.LogWarning("Connector {ConnectorId} is unavailable: Integration {IntegrationId} is {Status}; missing fields: {MissingFields}",
                    connector.Id, dependency, snapshot.Status, string.Join(", ", snapshot.MissingFields));
            }
            if (ready) { result.Add(new(connector.Id, connector.DisplayName, connector.AuthMethods, connector.RequiredIntegrations,
                connector.OptionalIntegrations, connector.Neurons.Select(t => t.Name).ToArray(), connector.Signals.Select(t => t.Name).ToArray(), connector.ExternalSystem)); }
        }
        return result.ToArray();
    }
}
