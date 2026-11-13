using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Identity.Local.Custody;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Authority.Connections;
using IntoChat.OS.Authority.Hosting;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Authority.Identity.Directory;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Authority.Integrations;
using IntoChat.OS.Authority.Secrets;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Authority;

public static class PlatformHosting
{
    public static void AddIntoChatOSPlatform(this ISiloBuilder silo)
    {
        var services = silo.Services;
        if (services.Any(service => service.ServiceType == typeof(PlatformRegistration))) { return; }
        services.AddSingleton<PlatformRegistration>();
        services.AddIdentity();
        services.TryAddSingleton<DeploymentStorageIdentity>();
        silo.AddStartupTask<IdentityStartup>();
        services.AddMasterKeyWrapper();
        // Pricing and the compute ledger are 0.8.0. External calls are not charged.
        services.TryAddSingleton<DigitalBrain.Capabilities.Compute.ModulePriceListRegistry>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<Storage.BrainStoreOptions>().BindConfiguration(Storage.BrainStoreOptions.Key)
            .PostConfigure<Microsoft.Extensions.Configuration.IConfiguration, IServiceProvider>((options, configuration, sp) => options.Directory = DigitalBrain.Runtime.Orleans.StateHome.Resolve(configuration, sp.GetService<DigitalBrain.Runtime.Orleans.RuntimeOptions>() ?? new() { HomeKey = "IntoChatOS:Home", DefaultHomeDirectoryName = ".intochat" }).State);
        services.AddSiloDiagnostics();
        services.TryAddSingleton<DigitalBrain.Identity.Local.Custody.IMasterKeyCustody>(provider => new Keys.ConfiguredMasterKeyCustody(
            System.Text.Encoding.UTF8.GetBytes(provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MasterKeyOptions>>().Value.MasterKey)));
        services.TryAddSingleton<Storage.BrainStorageKeys>();
        services.TryAddSingleton<EdgeGrantsCache>();
        services.TryAddSingleton<IEdgeGrantsCache>(sp => sp.GetRequiredService<EdgeGrantsCache>());
        services.AddSingleton<IConnectionBindings, Connections.ConnectionBindings>();
        services.TryAddSingleton<IBrainSecretSealer, Secrets.BrainSecretSealer>();
        services.TryAddSingleton<IntoChat.OS.Connections.Platform.Secrets.ISecretValueSource, Secrets.VaultSecretValueSource>();
        services.TryAddSingleton<IIdentityClient, Identity.LocalIdentityClient>();
        services.TryAddSingleton<IBrainOwnerDispatch, Identity.BrainOwnerDispatch>();
        services.TryAddSingleton<AuthorityNeurons>();
        services.TryAddSingleton<BrainBirths>();
        services.TryAddSingleton<IBrainBirths>(provider => provider.GetRequiredService<BrainBirths>());
        services.TryAddSingleton<PrincipalPasswords>();
        silo.AddStartupTask<Identity.OwnerBootstrap>(ServiceLifecycleStage.Active + 1);
        services.TryAddSingleton<IReadOnlyList<IntegrationSchema>>(provider =>
            IntegrationDiscovery.Collect(provider.GetRequiredService<ModuleInventory>().Types));
        services.TryAddSingleton<IReadOnlyList<ConnectorDefinition>>(provider =>
            ConnectorDiscovery.Collect(provider.GetRequiredService<ModuleInventory>(), provider.GetRequiredService<IReadOnlyList<IntegrationSchema>>()));
        services.TryAddSingleton(typeof(IModuleIntegrations<>), typeof(ModuleIntegrations<>));
        services.TryAddSingleton<IntoChat.OS.Connections.Platform.Entries.IBrainProviderKeys, Entries.BrainProviderKeys>();
        services.TryAddSingleton<ConnectorCatalog>();
        services.TryAddSingleton<ICapabilities, Capabilities>();
        services.AddGrpc();
        services.TryAddSingleton<IntoChat.OS.Connections.Surface.NeuronSurface>();
        silo.AddStartupTask<IntegrationSeeder>();
        services.TryAddSingleton<Identity.Authority.AuthorityMirror>();
        services.TryAddSingleton<IGrantPolicySource, GrainGrantPolicySource>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICallFilterStage, GrantCallFilterStage>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIncomingGrainCallFilter, SiloAccessCallFilter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, Hosting.AccessPolicyValidation>());
        silo.AddStartupTask<Hosting.AccessPolicyValidation>();
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
        services.TryAddSingleton<Connections.OAuth.OAuthLoginRequests>();
        services.TryAddSingleton<Connections.OAuth.OAuthTokenExchange>();
        services.TryAddSingleton<IOAuthTokenExchange>(provider => provider.GetRequiredService<Connections.OAuth.OAuthTokenExchange>());
        services.TryAddSingleton<Connections.OAuth.OAuthLoginFlow>();
        services.AddHttpClient(Connections.OAuth.OAuthTokenExchange.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddMeteredHttpClient(Connections.OAuth.OAuthTokenExchange.MeteredHttpClientName)
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<IHttpSurface, Connections.OAuth.OAuthCallback>();
        // "Request starting" lines carry the callback's query (code and state); never log them, whatever the host's level.
        services.AddLogging(logging => logging.AddFilter(Connections.OAuth.OAuthCallback.RequestLogCategory, LogLevel.Warning));
    }

    public static IEndpointRouteBuilder MapIntoChatOSPlatform(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapSecrets();
        endpoints.MapIntegrations();
        endpoints.MapConnectionNeurons();
        Entries.BrainEntriesEndpoints.MapBrainEntries(endpoints);
        endpoints.MapCapabilities();
        IdentityEndpoints.Map(endpoints);
        Hosting.RuntimeManagementEndpoints.Map(endpoints);
        endpoints.MapSiloDiagnostics();
        endpoints.MapGrpcService<Neurons.NeuronsService>();
        IntoChat.OS.Connections.Http.HandleRoutes.Map(endpoints);
        return endpoints;
    }

    private sealed class PlatformRegistration;
}
