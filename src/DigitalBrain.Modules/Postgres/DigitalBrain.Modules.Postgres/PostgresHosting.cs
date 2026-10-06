using DigitalBrain.AI.Agents;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Capacity;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Supabase;
using DigitalBrain.Supabase.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Orleans.Hosting;
using DigitalBrain.Postgres.Tables;

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
            .Configure<IConfiguration>((options, configuration) => configuration.PopulateModuleOptions("postgres", options))
            .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionName), "Postgres requires a connection name.")
            .ValidateOnStart();
        // Server
        services.TryAddSingleton<PostgresSourceRegistry>();
        services.TryAddSingleton<IPostgresSourceRegistry>(provider => provider.GetRequiredService<PostgresSourceRegistry>());
        services.AddSingleton<IIntegrationStartup>(provider => provider.GetRequiredService<PostgresSourceRegistry>());
        services.TryAddSingleton<IPostgresProvider, IntegrationPostgresProvider>();
        services.AddSingleton<ICapacityConfiguredSource>(provider =>
            new PostgresConfiguredSource(active: !provider.GetRequiredService<IOptions<PostgresModuleOptions>>().Value.ProvisionPerBrain));
        services.AddSingleton<ICapacityProvisioner>(provider =>
            provider.GetRequiredService<IOptions<PostgresModuleOptions>>().Value.ProvisionPerBrain
                ? new DockerPostgresProvisioner(provider.GetRequiredService<IModuleIntegrations<PostgresModule>>().Get("postgres"))
                : new InactivePostgresProvisioner());
        services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);
        // Tables
        services.TryAddSingleton<ITableStore, TableStore>();
        services.AddSingleton<IIncomingGrainCallFilter, TableLifecycleGuard>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<DigitalBrain.Apps.IAppStorageHook, TableStorageHook>());
        // Moves to Postgres.Windows in the next task.
        services.TryAddSingleton<PostgresAppResources>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRegistryInventoryProvider, PostgresInventoryProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<DigitalBrain.Framework.IRegistryResourceProvider, PostgresRegistryResources>());
        services.AddLiveTables();
        services.TryAddKeyedSingleton<ILiveTableSource>("postgres", (provider, _) =>
            new PostgresIntegrationLiveSource(provider.GetRequiredService<IPostgresSourceRegistry>(), PostgresCapacityKind.PlatformOrigin));
        services.TryAddKeyedSingleton<ILiveTableSource>(KeyedService.AnyKey, (provider, key) =>
            key is string name && name.StartsWith(PostgresAppResources.SourcePrefix, StringComparison.Ordinal) ? new PostgresAppLiveSource(
                () => provider.GetRequiredService<PostgresAppResources>().Resolve((string)key!, CancellationToken.None),
                origin => new PostgresIntegrationLiveSource(provider.GetRequiredService<IPostgresSourceRegistry>(), origin))
                : throw new DigitalBrain.Supabase.Tables.SupabaseTableSourceException("The requested table source is not configured."));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolFactory, PostgresTools>());
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
