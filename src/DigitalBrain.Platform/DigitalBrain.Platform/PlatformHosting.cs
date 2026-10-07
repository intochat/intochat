using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Platform.Capacity;
using DigitalBrain.Platform.Connections;
using DigitalBrain.Platform.Identity;
using DigitalBrain.Platform.Identity.Directory;
using DigitalBrain.Platform.Identity.Grants;
using DigitalBrain.Platform.Integrations;
using DigitalBrain.Platform.Secrets;
using DigitalBrain.Platform.Shipping;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Platform;

public static class PlatformHosting
{
    public static void AddDigitalBrainPlatform(this ISiloBuilder silo)
    {
        var services = silo.Services;
        if (services.Any(service => service.ServiceType == typeof(PlatformRegistration))) { return; }
        services.AddSingleton<PlatformRegistration>();
        services.AddIdentity();
        services.AddShippedContentPublishing();
        services.TryAddSingleton<DeploymentStorageIdentity>();
        silo.AddStartupTask<IdentityStartup>();
        silo.AddStartupTask<SeededAccountsStartup>();
        services.TryAddSingleton<DigitalBrain.Framework.Platform.Identity.IPublisherDirectory, PublisherDirectory>();
        services.AddMasterKeyWrapper();
        services.TryAddSingleton<IReadOnlyList<IntegrationSchema>>(provider =>
            IntegrationDiscovery.Collect(provider.GetRequiredService<ModuleInventory>().Types));
        services.TryAddSingleton<IReadOnlyList<ConnectorDefinition>>(provider =>
            ConnectorDiscovery.Collect(provider.GetRequiredService<ModuleInventory>(), provider.GetRequiredService<IReadOnlyList<IntegrationSchema>>()));
        services.TryAddSingleton(typeof(IModuleIntegrations<>), typeof(ModuleIntegrations<>));
        services.TryAddSingleton<ConnectorCatalog>();
        services.TryAddSingleton<ICapabilities, Capabilities>();
        silo.AddStartupTask<IntegrationSeeder>();
        services.AddCapacity();
        services.TryAddSingleton<IGrantPolicySource, GrainGrantPolicySource>();
        services.TryAddSingleton<IIdentity, PlatformIdentity>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICallFilterStage, GrantCallFilterStage>());
        services.AddOptions<ConnectionsOptions>().BindConfiguration(ConnectionsOptions.SectionName);
        // Silo singletons: a restart takes the pending logins with it, so a callback fails closed.
        services.TryAddSingleton<Connections.OAuth.OAuthLoginRequests>();
        services.TryAddSingleton<Connections.OAuth.OAuthTokenExchange>();
        services.TryAddSingleton<IOAuthTokenExchange>(provider => provider.GetRequiredService<Connections.OAuth.OAuthTokenExchange>());
        services.TryAddSingleton<Connections.OAuth.OAuthLoginFlow>();
        services.AddHttpClient(Connections.OAuth.OAuthTokenExchange.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<IHttpSurface, Connections.OAuth.OAuthCallback>();
        // "Request starting" lines carry the callback's query (code and state); never log them, whatever the host's level.
        services.AddLogging(logging => logging.AddFilter(Connections.OAuth.OAuthCallback.RequestLogCategory, LogLevel.Warning));
    }

    public static IEndpointRouteBuilder MapDigitalBrainPlatform(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapSecrets();
        endpoints.MapIntegrations();
        endpoints.MapConnectionNeurons();
        endpoints.MapCapabilities();
        IdentityEndpoints.Map(endpoints);
        return endpoints;
    }

    private sealed class PlatformRegistration;
}
