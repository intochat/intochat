using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Connections.Integrations;
using IntoChat.OS.Connections.Platform.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace IntoChat.OS.Authority.Integrations;

// Reconciles IntoChatOS:Integrations:{id}:{Field} into every registration on boot. A field is
// applied when it is absent from the registration, or when the stored value also came from the
// deployment and has moved (a relocated endpoint). A value a person set or cleared is never touched.
internal sealed class IntegrationSeeder(
    IConfiguration configuration,
    IReadOnlyList<IntegrationSchema> definitions,
    AuthorityNeurons neurons,
    ILogger<IntegrationSeeder> logger,
    ConnectorCatalog? connectors = null,
    IEnumerable<IIntegrationStartup>? consumers = null,
    EdgeAuthority? edge = null) : IStartupTask
{
    private const int MaxSeedAttempts = 3;

    public Task Execute(CancellationToken cancellationToken) => SeedAsync(cancellationToken);

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var failureCount = 0;
        foreach (var definition in definitions)
        {
            for (var attempt = 1; attempt <= MaxSeedAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await SeedOneAsync(definition, cancellationToken);
                    break;
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    if (attempt == MaxSeedAttempts)
                    {
                        failureCount++;
                        logger.LogError("Seeding integration {IntegrationId} failed after {Attempts} attempts ({ErrorType}).",
                            definition.Id, attempt, error.GetType().Name);
                        break;
                    }
                    var delay = TimeSpan.FromSeconds(1 << (attempt - 1));
                    logger.LogWarning("Seeding integration {IntegrationId} failed on attempt {Attempt} ({ErrorType}); retrying in {Delay}.",
                        definition.Id, attempt, error.GetType().Name, delay);
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation("Integration seeding completed with {FailureCount} failures.", failureCount);
        foreach (var consumer in consumers ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            await consumer.InitializeAsync(cancellationToken);
        }
        if (connectors is not null) { await connectors.ListAsync(cancellationToken); }
    }

    private async Task SeedOneAsync(IntegrationSchema definition, CancellationToken cancellationToken)
    {
        var values = definition.AllFields
            .Select(field => (Field: field, Value: ResolveValue(definition, field)))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Field, pair => pair.Value!, StringComparer.Ordinal);
        if (values.Count == 0)
        {
            return;
        }

        using var _ = edge?.Enter(edge.Module("integrations.seeder", new BrainId("system")));
        var registration = neurons.Integration(definition.Id);
        var after = await registration.Seed(new ConfigureIntegration { Values = values }).WaitAsync(cancellationToken);
        logger.LogInformation("Integration {IntegrationId} is {Status} after seeding.", definition.Id, after.Status);
    }

    private string? ResolveValue(IntegrationSchema definition, string field)
    {
        var configured = configuration[definition.Key(field)];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return (definition.Id, field) switch
        {
            ("google-oauth", "ClientId") => configuration["IntoChatOS:Modules:auth:Secrets:google-client-id"],
            ("google-oauth", "ClientSecret") => configuration["IntoChatOS:Modules:auth:Secrets:google-client-secret"],
            _ => null,
        };
    }
}
