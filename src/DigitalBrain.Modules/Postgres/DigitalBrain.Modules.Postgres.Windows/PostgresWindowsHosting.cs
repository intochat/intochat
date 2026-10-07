using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Tools;
using DigitalBrain.Framework;
using DigitalBrain.Supabase;
using DigitalBrain.Supabase.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace DigitalBrain.Postgres.Windows;

public static class PostgresWindowsHosting
{
    public static ISiloBuilder AddPostgresWindows(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        var services = silo.Services;
        services.TryAddSingleton<PostgresAppResources>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRegistryInventoryProvider, PostgresInventoryProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRegistryResourceProvider, PostgresRegistryResources>());
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
}
