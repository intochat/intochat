using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace DigitalBrain.Platform.Integrations;

// Seeds DigitalBrain:Integrations:{id}:{Field} into registrations that are still Unconfigured. A
// Partial registration is an operator's edit in progress and is never touched.
internal sealed class IntegrationSeeder(
    IConfiguration configuration,
    IReadOnlyList<IntegrationSchema> definitions,
    IGrainFactory grains,
    ILogger<IntegrationSeeder> logger,
    ConnectorCatalog? connectors = null,
    IEnumerable<IIntegrationStartup>? consumers = null) : IStartupTask
{
    public Task Execute(CancellationToken cancellationToken) => SeedAsync(cancellationToken);

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await SeedOneAsync(definition);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(error, "Seeding integration {IntegrationId} failed.", definition.Id);
            }
        }
        foreach (var consumer in consumers ?? []) { await consumer.InitializeAsync(cancellationToken); }
        if (connectors is not null) { await connectors.ListAsync(cancellationToken); }
    }

    private async Task SeedOneAsync(IntegrationSchema definition)
    {
        var values = definition.AllFields
            .Select(field => (Field: field, Value: configuration[definition.Key(field)]))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Field, pair => pair.Value!, StringComparer.Ordinal);
        if (values.Count == 0)
        {
            return;
        }

        var registration = grains.GetGrain<IIntegration>(IntegrationNeuron.KeyPrefix + definition.Id);
        var after = await registration.SeedIfUnconfigured(new ConfigureIntegration { Values = values });
        logger.LogInformation("Integration {IntegrationId} is {Status} after seeding.", definition.Id, after.Status);
    }
}
