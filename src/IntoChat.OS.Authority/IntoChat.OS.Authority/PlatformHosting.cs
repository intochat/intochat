using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Platform.Connections;
using IntoChat.OS.Framework.Platform.Identity;
using IntoChat.OS.Framework.Platform.Integrations;
using IntoChat.OS.Authority.Connections;
using IntoChat.OS.Authority.Hosting;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Authority.Identity.Directory;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Authority.Integrations;
using IntoChat.OS.Authority.Secrets;
using IntoChat.OS.Authority.Workshop;
using IntoChat.OS.Sdk.Http.Compute;
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
        services.TryAddSingleton<IntoChat.OS.Framework.Compute.ICostMeter, Compute.CostMeter>();
        services.TryAddSingleton<IntoChat.OS.Framework.Compute.IComputeReader, Compute.ComputeReader>();
        services.TryAddSingleton<IntoChat.OS.Sdk.Http.Compute.IExternalCallContextAccessor, IntoChat.OS.Framework.Compute.ExternalCallContextAccessor>();
        silo.AddIncomingGrainCallFilter<Compute.CircleShareGuard>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Http.IHttpMessageHandlerBuilderFilter, IntoChat.OS.Framework.Compute.MeteredHttpHandlerFilter>());
        services.AddOptions<Compute.FeeOptions>().BindConfiguration(Compute.FeeOptions.Key)
            .Validate(value => value.PlatformPercent >= 0, "Platform fee must be nonnegative.").ValidateOnStart();
        services.TryAddSingleton<IntoChat.OS.Sdk.Compute.ModulePriceListRegistry>();
        services.AddOptions<Compute.ComputeOptions>().BindConfiguration(Compute.ComputeOptions.Key)
            .Validate(value => { value.Validate(); return true; })
            .Validate<IntoChat.OS.Sdk.Compute.ModulePriceListRegistry>((value, registry) =>
            {
                foreach (var allowance in value.AllowedUnpricedOperations) { registry.GetOperation(allowance.Operation); }
                return true;
            }).ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<Storage.BrainStoreOptions>().BindConfiguration(Storage.BrainStoreOptions.Key);
        services.AddSiloDiagnostics();
        services.TryAddSingleton<IntoChat.OS.Framework.Parts.IMasterKeyCustody>(provider => new Keys.ConfiguredMasterKeyCustody(
            System.Text.Encoding.UTF8.GetBytes(provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MasterKeyOptions>>().Value.MasterKey)));
        services.TryAddSingleton<Storage.BrainStorageKeys>();
        services.TryAddSingleton<Storage.IBrainJournal, Storage.BrainJournal>();
        services.AddSingleton<IConnectionBindings, Connections.ConnectionBindings>();
        services.TryAddSingleton<IBrainSecretSealer, Secrets.BrainSecretSealer>();
        services.TryAddSingleton<IntoChat.OS.Framework.Platform.Secrets.ISecretValueSource, Secrets.VaultSecretValueSource>();
        services.TryAddSingleton<IIdentityClient, Identity.LocalIdentityClient>();
        services.TryAddSingleton<IBrainOwnerDispatch, Identity.BrainOwnerDispatch>();
        services.TryAddSingleton<IntoChat.OS.Framework.Platform.Marketplace.IMarketplaceClient, Marketplace.LocalMarketplaceClient>();
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
        services.TryAddSingleton<ConnectorCatalog>();
        services.TryAddSingleton<ICapabilities, Capabilities>();
        services.AddGrpc();
        services.TryAddSingleton<IntoChat.OS.Framework.Surface.NeuronSurface>();
        silo.AddStartupTask<IntegrationSeeder>();
        services.TryAddSingleton<Identity.Authority.AuthorityMirror>();
        services.TryAddSingleton<IGrantPolicySource, GrainGrantPolicySource>();
        services.TryAddSingleton<IIdentity, PlatformIdentity>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICallFilterStage, GrantCallFilterStage>());
        services.AddOptions<ConnectionsOptions>().BindConfiguration(ConnectionsOptions.SectionName);
        // The Workshop edits the OS's own codebase; empty root means it is off until a host opts in.
        services.AddOptions<WorkshopOptions>().BindConfiguration(IntoChat.OS.Framework.Configuration.IntoChatOSConfiguration.WorkshopSectionName);
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
