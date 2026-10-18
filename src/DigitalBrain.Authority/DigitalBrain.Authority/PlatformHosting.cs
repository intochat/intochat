using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Authority.Connections;
using DigitalBrain.Authority.Hosting;
using DigitalBrain.Authority.Identity;
using DigitalBrain.Authority.Identity.Directory;
using DigitalBrain.Authority.Identity.Grants;
using DigitalBrain.Authority.Integrations;
using DigitalBrain.Authority.Secrets;
using DigitalBrain.Authority.Workshop;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Authority;

public static class PlatformHosting
{
    public static void AddDigitalBrainPlatform(this ISiloBuilder silo)
    {
        var services = silo.Services;
        if (services.Any(service => service.ServiceType == typeof(PlatformRegistration))) { return; }
        services.AddSingleton<PlatformRegistration>();
        services.AddIdentity();
        services.TryAddSingleton<DeploymentStorageIdentity>();
        silo.AddStartupTask<IdentityStartup>();
        services.AddMasterKeyWrapper();
        services.TryAddSingleton<DigitalBrain.Framework.Compute.ICostMeter, Compute.CostMeter>();
        services.TryAddSingleton<DigitalBrain.Framework.Compute.IComputeReader, Compute.ComputeReader>();
        silo.AddIncomingGrainCallFilter<Compute.CircleShareGuard>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Http.IHttpMessageHandlerBuilderFilter, DigitalBrain.Framework.Compute.MeteredHttpHandlerFilter>());
        services.AddOptions<Compute.FeeOptions>().BindConfiguration(Compute.FeeOptions.Key);
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<Storage.BrainStoreOptions>().BindConfiguration(Storage.BrainStoreOptions.Key);
        services.AddSiloDiagnostics();
        services.TryAddSingleton<DigitalBrain.Framework.Parts.IMasterKeyCustody>(provider => new Keys.ConfiguredMasterKeyCustody(
            System.Text.Encoding.UTF8.GetBytes(provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MasterKeyOptions>>().Value.MasterKey)));
        services.TryAddSingleton<Storage.BrainStorageKeys>();
        services.AddSingleton<IConnectionBindings, Connections.ConnectionBindings>();
        services.TryAddSingleton<IBrainSecretSealer, Secrets.BrainSecretSealer>();
        services.TryAddSingleton<IIdentityClient, Identity.LocalIdentityClient>();
        services.TryAddSingleton<DigitalBrain.Framework.Platform.Marketplace.IMarketplaceClient, Marketplace.LocalMarketplaceClient>();
        services.TryAddSingleton<AuthorityNeurons>();
        services.TryAddSingleton<BrainBirths>();
        services.TryAddSingleton<PrincipalPasswords>();
        silo.AddStartupTask<Identity.OwnerBootstrap>(ServiceLifecycleStage.Active + 1);
        services.TryAddSingleton<IReadOnlyList<IntegrationSchema>>(provider =>
            IntegrationDiscovery.Collect(provider.GetRequiredService<ModuleInventory>().Types));
        services.TryAddSingleton<IReadOnlyList<ConnectorDefinition>>(provider =>
            ConnectorDiscovery.Collect(provider.GetRequiredService<ModuleInventory>(), provider.GetRequiredService<IReadOnlyList<IntegrationSchema>>()));
        services.TryAddSingleton(typeof(IModuleIntegrations<>), typeof(ModuleIntegrations<>));
        services.TryAddSingleton<ConnectorCatalog>();
        services.TryAddSingleton<ICapabilities, Capabilities>();
        services.AddGrpc();
        services.TryAddSingleton<DigitalBrain.Framework.Surface.NeuronSurface>();
        silo.AddStartupTask<IntegrationSeeder>();
        services.TryAddSingleton<Identity.Authority.AuthorityMirror>();
        services.TryAddSingleton<IGrantPolicySource, GrainGrantPolicySource>();
        services.TryAddSingleton<IIdentity, PlatformIdentity>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICallFilterStage, GrantCallFilterStage>());
        services.AddOptions<ConnectionsOptions>().BindConfiguration(ConnectionsOptions.SectionName);
        // The Workshop edits the OS's own codebase; empty root means it is off until a host opts in.
        services.AddOptions<WorkshopOptions>().BindConfiguration(DigitalBrain.Framework.Configuration.DigitalBrainConfiguration.WorkshopSectionName);
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
        Compute.ComputeEndpoints.MapCompute(endpoints);
        Signals.SignalsEndpoints.MapSignals(endpoints);
        endpoints.MapCapabilities();
        IdentityEndpoints.Map(endpoints);
        Hosting.RuntimeManagementEndpoints.Map(endpoints);
        endpoints.MapSiloDiagnostics();
        foreach (var extension in endpoints.ServiceProvider.GetServices<IAuthorityHttpExtension>()) { extension.Map(endpoints); }
        endpoints.MapGrpcService<Neurons.NeuronsService>();
        return endpoints;
    }

    private sealed class PlatformRegistration;
}
