using IntoChat.OS.Framework.Platform.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace IntoChat.OS.Authority.Integrations;

// Reconciles DigitalBrain:Integrations:{id}:{Field} into every registration on boot. A field is
// applied when it is absent from the registration, or when the stored value also came from the
// deployment and has moved (a relocated endpoint). A value a person set or cleared is never touched.
internal sealed class IntegrationSeeder(
    IConfiguration configuration,
    IReadOnlyList<IntegrationSchema> definitions,
    AuthorityNeurons neurons,
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

        var registration = neurons.Integration(definition.Id);
        var after = await registration.Seed(new ConfigureIntegration { Values = values });
        logger.LogInformation("Integration {IntegrationId} is {Status} after seeding.", definition.Id, after.Status);
    }
}
