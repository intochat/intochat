using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Kernel;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Identity.Integrations;

// A connector's neurons and signals are its kernel type entry (DigitalBrain.Kernel.TypeKind.Connector, named by Id), not copied here.
internal sealed record ConnectorCatalogEntry(
    string Id,
    string DisplayName,
    string[] AuthMethods,
    string[] RequiredIntegrations,
    string[] OptionalIntegrations,
    string ExternalSystem,
    string[] AvailableAuthMethods);

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
            // Startup lists the catalog before any caller exists: the read then runs as the identity module.
            using var own = neurons.Own();
            var dependencies = connectors
                .SelectMany(c => c.RequiredIntegrations
                    .Concat(c.OptionalIntegrations)
                    .Concat(c.OAuthMethods.Select(m => m.IntegrationId))
                    .Concat(c.DeviceMethods.Select(m => m.IntegrationId).Where(id => !string.IsNullOrWhiteSpace(id)).Cast<string>()))
                .Distinct(StringComparer.Ordinal);

            foreach (var dependency in dependencies)
            {
                var snapshot = await neurons.Integration(dependency).Read().WaitAsync(cancellationToken);
                if (!_snapshots.TryGetValue(dependency, out var previous) || previous.Revision != snapshot.Revision || previous.Status != snapshot.Status)
                { _snapshots[dependency] = snapshot; }
            }

            var result = new List<ConnectorCatalogEntry>();
            foreach (var connector in connectors)
            {
                var availableMethods = connector.AuthMethods
                    .Where(authMethodId => IsMethodAvailable(connector, authMethodId, _snapshots))
                    .ToArray();
                var ready = availableMethods.Length > 0;
                if (!ready && (!_availability.TryGetValue(connector.Id, out var wasReady) || wasReady))
                {
                    var unavailable = connector.RequiredIntegrations
                        .Concat(connector.OAuthMethods.Select(m => m.IntegrationId))
                        .Concat(connector.DeviceMethods.Select(m => m.IntegrationId).Where(id => !string.IsNullOrWhiteSpace(id)).Cast<string>())
                        .Distinct(StringComparer.Ordinal)
                        .Where(id => _snapshots.TryGetValue(id, out var snap) && snap.Status != IntegrationStatus.Ready)
                        .Select(id => _snapshots[id])
                        .ToArray();

                    logger.LogWarning("Connector {ConnectorId} is unavailable: {Dependencies}", connector.Id,
                        string.Join("; ", unavailable.Select(s => $"Integration {s.IntegrationId} is {s.Status}; missing fields: {string.Join(", ", s.MissingFields)}")));
                }

                _availability[connector.Id] = ready;
                if (!ready)
                {
                    _entries.Remove(connector.Id);
                    continue;
                }

                if (!_entries.TryGetValue(connector.Id, out var entry)
                    || !entry.AvailableAuthMethods.SequenceEqual(availableMethods, StringComparer.Ordinal))
                {
                    entry = new(
                        connector.Id,
                        connector.DisplayName,
                        connector.AuthMethods,
                        connector.RequiredIntegrations,
                        connector.OptionalIntegrations,
                        connector.ExternalSystem,
                        availableMethods);
                    _entries[connector.Id] = entry;
                }

                result.Add(entry);
            }

            return result.ToArray();
        }
        finally { _refresh.Release(); }
    }

    internal static bool IsMethodAvailable(
        ConnectorDefinition connector,
        string authMethodId,
        IReadOnlyDictionary<string, IntegrationSnapshot> snapshots)
    {
        if (connector.OAuthMethods.FirstOrDefault(m => string.Equals(m.Id, authMethodId, StringComparison.Ordinal)) is { } oauth)
        {
            return snapshots.TryGetValue(oauth.IntegrationId, out var snapshot) && snapshot.Status == IntegrationStatus.Ready;
        }

        if (connector.DeviceMethods.FirstOrDefault(m => string.Equals(m.Id, authMethodId, StringComparison.Ordinal)) is { } device)
        {
            if (!string.IsNullOrWhiteSpace(device.DefaultClientId))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(device.IntegrationId)
                || !snapshots.TryGetValue(device.IntegrationId, out var snapshot))
            {
                return false;
            }

            // A public device flow only needs a non-secret ClientId; it never requires confidential host secrets (e.g. PrivateKeyPem).
            return snapshot.Status == IntegrationStatus.Ready
                || (snapshot.Settings.TryGetValue(device.ClientIdField, out var clientId) && !string.IsNullOrWhiteSpace(clientId));
        }

        // Auth methods that require no confidential host IntegrationSchema (e.g. pat, bearer-token) are always available.
        return true;
    }
}
