using DigitalBrain.Capabilities.Connections.Connectors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Modules.Identity.Integrations;

internal static class IntegrationCatalog
{
    public static async Task<IntegrationCatalogEntry[]> ListAsync(
        IReadOnlyList<IntegrationSchema> definitions, AuthorityNeurons neurons, CancellationToken cancellationToken)
    {
        using var own = neurons.Own();
        var entries = new List<IntegrationCatalogEntry>(definitions.Count);
        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await neurons.Integration(definition.Id).Read();
            entries.Add(new IntegrationCatalogEntry(definition.Id, definition.DisplayName, snapshot.Status.ToString(), snapshot.MissingFields));
        }

        return [.. entries];
    }
}
