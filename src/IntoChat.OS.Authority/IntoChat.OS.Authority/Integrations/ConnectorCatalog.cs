using IntoChat.OS.Connections.Platform.Integrations;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Authority.Integrations;

// A connector's neurons and signals are its kernel type entry (DigitalBrain.TypeKind.Connector, named by Id), not copied here.
internal sealed record ConnectorCatalogEntry(string Id, string DisplayName, string[] AuthMethods, string[] RequiredIntegrations, string[] OptionalIntegrations, string ExternalSystem);

internal sealed class ConnectorCatalog(IReadOnlyList<ConnectorDefinition> connectors, AuthorityNeurons neurons, ILogger<ConnectorCatalog> logger)
{
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly Dictionary<string, IntegrationSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _availability = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConnectorCatalogEntry> _entries = new(StringComparer.Ordinal);

    public async Task<ConnectorCatalogEntry[]> ListAsync(CancellationToken cancellationToken)
    {
        await _refresh.WaitAsync(cancellationToken);
        try
        {
            // Read every distinct dependency for freshness; only replace its cached snapshot on change.
            foreach (var dependency in connectors.SelectMany(c => c.RequiredIntegrations).Distinct(StringComparer.Ordinal))
            {
                var snapshot = await neurons.Integration(dependency).Read().WaitAsync(cancellationToken);
                if (!_snapshots.TryGetValue(dependency, out var previous) || previous.Revision != snapshot.Revision || previous.Status != snapshot.Status)
                { _snapshots[dependency] = snapshot; }
            }

            var result = new List<ConnectorCatalogEntry>();
            foreach (var connector in connectors)
            {
                var unavailable = connector.RequiredIntegrations.Select(id => _snapshots[id]).Where(s => s.Status != IntegrationStatus.Ready).ToArray();
                var ready = unavailable.Length == 0;
                if (!ready && (!_availability.TryGetValue(connector.Id, out var wasReady) || wasReady))
                {
                    logger.LogWarning("Connector {ConnectorId} is unavailable: {Dependencies}", connector.Id,
                        string.Join("; ", unavailable.Select(s => $"Integration {s.IntegrationId} is {s.Status}; missing fields: {string.Join(", ", s.MissingFields)}")));
                }
                _availability[connector.Id] = ready;
                if (!ready) { continue; }
                if (!_entries.TryGetValue(connector.Id, out var entry))
                {
                    entry = new(connector.Id, connector.DisplayName, connector.AuthMethods, connector.RequiredIntegrations,
                        connector.OptionalIntegrations, connector.ExternalSystem);
                    _entries.Add(connector.Id, entry);
                }
                result.Add(entry);
            }
            return result.ToArray();
        }
        finally { _refresh.Release(); }
    }
}
