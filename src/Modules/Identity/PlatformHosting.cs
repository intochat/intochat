using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Identity.Local.Custody;
using DigitalBrain.Runtime.Http.Surface;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Modules;
using IntoChat.OS.Modules.Identity.Connections;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Directory;
using IntoChat.OS.Modules.Identity.Grants;
using IntoChat.OS.Modules.Identity.Integrations;
using IntoChat.OS.Modules.Identity.Secrets;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Grants;

namespace IntoChat.OS.Modules.Identity;

public static class PlatformHosting
{
    public static void AddIntoChatOSPlatform(this ISiloBuilder silo)
    {
        var services = silo.Services;
        if (services.Any(service => service.ServiceType == typeof(PlatformRegistration))) { return; }
        services.AddSingleton<PlatformRegistration>();
        DigitalBrain.Invocation.RuntimeProtocol.Register(IntoChat.OS.Modules.Identity.Codes.CodesJson.Default);
        services.AddIdentity();
        services.TryAddSingleton<IntoChat.OS.Modules.Identity.Contracts.Codes.ICodeBook, IntoChat.OS.Modules.Identity.Codes.CodeBook>();
        services.TryAddSingleton<DeploymentStorageIdentity>();
        silo.AddStartupTask<IdentityStartup>();
        services.AddMasterKeyWrapper();
        // Pricing and the compute ledger are 0.8.0. External calls are not charged.
        services.TryAddSingleton<DigitalBrain.Capabilities.Compute.ModulePriceListRegistry>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<EdgeGrantsCache>();
        services.TryAddSingleton<IEdgeGrantsCache>(sp => sp.GetRequiredService<EdgeGrantsCache>());
        services.AddSingleton<IConnectionBindings, IntoChat.OS.Modules.Identity.Connections.ConnectionBindings>();
        services.TryAddSingleton<IBrainSecretSealer, IntoChat.OS.Modules.Identity.Secrets.BrainSecretSealer>();
        services.TryAddSingleton<DigitalBrain.Capabilities.Secrets.ISecretValueSource, IntoChat.OS.Modules.Identity.Secrets.VaultSecretValueSource>();
        services.TryAddSingleton<IIdentityClient, IntoChat.OS.Modules.Identity.Accounts.LocalIdentityClient>();
        services.TryAddSingleton<IBrainOwnerDispatch, IntoChat.OS.Modules.Identity.Accounts.BrainOwnerDispatch>();
        services.TryAddSingleton<AuthorityNeurons>();
        services.TryAddSingleton<BrainBirths>();
        services.TryAddSingleton<IBrainBirths>(provider => provider.GetRequiredService<BrainBirths>());
        services.TryAddSingleton<PrincipalPasswords>();
        silo.AddStartupTask<IntoChat.OS.Modules.Identity.Accounts.OwnerBootstrap>(ServiceLifecycleStage.Active + 1);
        services.TryAddSingleton<IReadOnlyList<IntegrationSchema>>(provider =>
            IntegrationDiscovery.Collect(provider.GetRequiredService<ModuleInventory>().Types));
        services.TryAddSingleton<IReadOnlyList<ConnectorDefinition>>(provider =>
            ConnectorDiscovery.Collect(provider.GetRequiredService<ModuleInventory>(), provider.GetRequiredService<IReadOnlyList<IntegrationSchema>>()));
        services.TryAddSingleton(typeof(IModuleIntegrations<>), typeof(ModuleIntegrations<>));
        services.TryAddSingleton<IntoChat.OS.Modules.Identity.Contracts.Entries.IBrainProviderKeys, IntoChat.OS.Modules.Identity.Entries.BrainProviderKeys>();
        services.TryAddSingleton<ConnectorCatalog>();
        services.TryAddSingleton<ICapabilities, Capabilities>();
        silo.AddStartupTask<IntegrationSeeder>();
        services.TryAddSingleton<IntoChat.OS.Modules.Identity.Authority.AuthorityMirror>();
        services.TryAddSingleton<IGrantPolicySource, GrainGrantPolicySource>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICallFilterStage, GrantCallFilterStage>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIncomingGrainCallFilter, SiloAccessCallFilter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, IntoChat.OS.Modules.Identity.Hosting.AccessPolicyValidation>());
        silo.AddStartupTask<IntoChat.OS.Modules.Identity.Hosting.AccessPolicyValidation>();
        services.AddOptions<ConnectionsOptions>().BindConfiguration(ConnectionsOptions.SectionName)
            .PostConfigure(options =>
            {
                if (options.PublicOrigin is { Host: "localhost" } origin
                    && string.Equals(origin.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"), StringComparison.Ordinal))
                {
                    options.PublicOrigin = new Uri("http://localhost:5080");
                }
            });
        // Silo singletons: a restart takes the pending logins with it, so a callback fails closed.
        services.TryAddSingleton<IntoChat.OS.Modules.Identity.Connections.OAuth.OAuthLoginRequests>();
        services.TryAddSingleton<IntoChat.OS.Modules.Identity.Connections.OAuth.OAuthTokenExchange>();
        services.TryAddSingleton<IOAuthTokenExchange>(provider => provider.GetRequiredService<IntoChat.OS.Modules.Identity.Connections.OAuth.OAuthTokenExchange>());
        services.TryAddSingleton<IntoChat.OS.Modules.Identity.Connections.OAuth.OAuthLoginFlow>();
        services.AddHttpClient(IntoChat.OS.Modules.Identity.Connections.OAuth.OAuthTokenExchange.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddMeteredHttpClient(IntoChat.OS.Modules.Identity.Connections.OAuth.OAuthTokenExchange.MeteredHttpClientName)
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<IHttpSurface, IntoChat.OS.Modules.Identity.Connections.OAuth.OAuthCallback>();
        // "Request starting" lines carry the callback's query (code and state); never log them, whatever the host's level.
        services.AddLogging(logging => logging.AddFilter(IntoChat.OS.Modules.Identity.Connections.OAuth.OAuthCallback.RequestLogCategory, LogLevel.Warning));
    }

    public static IEndpointRouteBuilder MapIntoChatOSPlatform(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapSecrets();
        endpoints.MapIntegrations();
        endpoints.MapConnectionNeurons();
        IntoChat.OS.Modules.Identity.Entries.BrainEntriesEndpoints.MapBrainEntries(endpoints);
        endpoints.MapCapabilities();
        IdentityEndpoints.Map(endpoints);
        IntoChat.OS.Modules.Identity.Handles.HandleRoutes.Map(endpoints);
        return endpoints;
    }

    private sealed class PlatformRegistration;
}
