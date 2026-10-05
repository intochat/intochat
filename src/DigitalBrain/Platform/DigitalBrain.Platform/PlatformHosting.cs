using DigitalBrain.Enforcement;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Framework.Platform.Integrations.Accounts;
using DigitalBrain.Platform.Capacity;
using DigitalBrain.Platform.Identity;
using DigitalBrain.Platform.Identity.Directory;
using DigitalBrain.Platform.Identity.Grants;
using DigitalBrain.Platform.Integrations;
using DigitalBrain.Platform.Integrations.Accounts;
using DigitalBrain.Platform.Secrets;
using DigitalBrain.Platform.Shipping;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace DigitalBrain.Platform;

public static class PlatformHosting
{
    public static void AddDigitalBrainPlatform(this ISiloBuilder silo)
    {
        var services = silo.Services;
        if (services.Any(service => service.ServiceType == typeof(PlatformRegistration))) { return; }
        services.AddSingleton<PlatformRegistration>();
        services.TryAddSingleton<IRegistry, Discovery.PlatformRegistry>();
        services.TryAddSingleton<Discovery.ActivationCensus>();
        services.TryAddSingleton<IActivationCensus>(provider => provider.GetRequiredService<Discovery.ActivationCensus>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRegistryInventoryProvider, Discovery.ConnectionsInventoryProvider>());
        services.AddIdentity();
        services.AddShippedContentPublishing();
        services.AddOptions<IdentityMigrationOptions>().BindConfiguration(IdentityMigrationOptions.SectionName);
        services.TryAddSingleton<DeploymentStorageIdentity>();
        silo.AddStartupTask<IdentityMigrationStartup>();
        services.TryAddSingleton<DigitalBrain.Framework.Platform.Auth.ITokenHandoff, Auth.TokenHandoff>();
        services.TryAddSingleton<DigitalBrain.Framework.Platform.Auth.IOAuthCredentials, Auth.OAuthCredentials>();
        services.AddMasterKeyWrapper();
        services.TryAddSingleton<IReadOnlyList<IntegrationSchema>>(provider =>
            IntegrationDiscovery.Collect(provider.GetRequiredService<ModuleInventory>().Types));
        services.TryAddSingleton<IReadOnlyList<ConnectorDefinition>>(provider =>
            ConnectorDiscovery.Collect(provider.GetRequiredService<ModuleInventory>(), provider.GetRequiredService<IReadOnlyList<IntegrationSchema>>()));
        services.TryAddSingleton(typeof(IModuleIntegrations<>), typeof(ModuleIntegrations<>));
        services.TryAddSingleton(typeof(IModuleConnectionCredentials<>), typeof(ModuleConnectionCredentials<>));
        services.TryAddSingleton<IConnectionProbe, CredentialPresenceProbe>();
        services.TryAddSingleton<ConnectionCredentialProbe>();
        services.TryAddSingleton<IntegrationCredentials>();
        services.TryAddSingleton<ConnectorCatalog>();
        services.AddSingleton<IIntegrationStartup, PostgresMigrationStartup>();
        services.TryAddSingleton<ICapabilities, Capabilities>();
        silo.AddStartupTask<IntegrationSeeder>();
        services.AddCapacity();
        services.TryAddSingleton<IGrantPolicySource, GrainGrantPolicySource>();
        services.TryAddSingleton<IIdentity, PlatformIdentity>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICallFilterStage, GrantCallFilterStage>());
    }

    public static IEndpointRouteBuilder MapDigitalBrainPlatform(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapSecrets();
        endpoints.MapIntegrations();
        endpoints.MapConnections();
        endpoints.MapCapabilities();
        IdentityEndpoints.Map(endpoints);
        return endpoints;
    }

    private sealed class PlatformRegistration;
}
