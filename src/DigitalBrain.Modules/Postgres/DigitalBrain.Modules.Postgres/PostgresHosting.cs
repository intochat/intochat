using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Orleans.Hosting;
using DigitalBrain.Postgres.Tables;
using DigitalBrain.Framework.Configuration;

namespace DigitalBrain.Postgres;

public static class PostgresHosting
{
    public const string DataSourceKey = "DigitalBrain.Postgres";

    public static ISiloBuilder AddPostgres(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        var services = silo.Services;
        if (services.Any(descriptor => descriptor.ServiceType == typeof(PostgresRegistration))) { return silo; }
        services.AddSingleton<PostgresRegistration>();
        services.AddOptions<PostgresModuleOptions>()
            .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionName), "Postgres requires a connection name.")
            .ValidateOnStart();
        // Server
        services.TryAddSingleton<PostgresSourceRegistry>();
        services.TryAddSingleton<IPostgresSourceRegistry>(provider => provider.GetRequiredService<PostgresSourceRegistry>());
        services.AddSingleton<IIntegrationStartup>(provider => provider.GetRequiredService<PostgresSourceRegistry>());
        services.TryAddSingleton<IPostgresProvider, IntegrationPostgresProvider>();
        services.TryAddSingleton<IConnectionPostgresProviderFactory, ConnectionPostgresProviderFactory>();
        services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);
        // Tables
        services.TryAddSingleton<ITableStore, TableStore>();
        services.TryAddSingleton(provider => new BrainDatabases(provider.GetRequiredService<IModuleIntegrations<PostgresModule>>().Get("postgres")));
        services.TryAddSingleton<ITablePlacements, TablePlacements>();
        services.AddSingleton<IIncomingGrainCallFilter, TableLifecycleGuard>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<DigitalBrain.Apps.IAppStorageHook, TableStorageHook>());
        return silo;
    }

    private sealed class PostgresRegistration;

    private sealed class PostgresHealthCheck(IPostgresProvider provider) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var connection = await provider.PingAsync(cancellationToken).ConfigureAwait(false);
            return connection.Connected
                ? HealthCheckResult.Healthy("Postgres is reachable.")
                : HealthCheckResult.Unhealthy("Postgres is unreachable.");
        }
    }
}
