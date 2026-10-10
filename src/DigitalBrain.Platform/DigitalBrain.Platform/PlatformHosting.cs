using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Platform.Connections;
using DigitalBrain.Platform.Identity;
using DigitalBrain.Platform.Identity.Directory;
using DigitalBrain.Platform.Identity.Grants;
using DigitalBrain.Platform.Integrations;
using DigitalBrain.Platform.Secrets;
using DigitalBrain.Platform.Workshop;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Platform;

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
        silo.AddIncomingGrainCallFilter<Compute.CircleShareGuard>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Http.IHttpMessageHandlerBuilderFilter, DigitalBrain.Framework.Compute.MeteredHttpHandlerFilter>());
        services.AddOptions<Compute.FeeOptions>().BindConfiguration(Compute.FeeOptions.Key);
        services.AddOptions<Mesh.MeshOptions>().BindConfiguration(Mesh.MeshOptions.Key)
            .Validate(mesh => { mesh.Validate(); return true; }).ValidateOnStart();
        services.TryAddSingleton<DigitalBrain.Framework.Mesh.IMeshStore, Mesh.MeshStore>();
        services.AddOptions<Parts.PartsOptions>().BindConfiguration(Parts.PartsOptions.Key)
            .Validate(parts => { parts.Validate(); return true; }).ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(provider => Sequencer.MachineIdentity.LoadOrCreate(Path.Combine(
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Parts.PartsOptions>>().Value.MachineDirectory, "identity.key")));
        services.TryAddSingleton(provider => new Sequencer.SeenVersions(Path.Combine(
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Parts.PartsOptions>>().Value.MachineDirectory, "seen.json")));
        // The custody root is the same configured master key the owner-key wrapper derives from.
        services.TryAddSingleton<Keys.IMasterKeyCustody>(provider => new Keys.ConfiguredMasterKeyCustody(
            System.Text.Encoding.UTF8.GetBytes(provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MasterKeyOptions>>().Value.MasterKey)));
        services.TryAddSingleton<DigitalBrain.Framework.Parts.IPartKeys, Keys.PartKeys>();
        // The machine reaches the trust grains only through these clients; Stage A hosts them in this silo.
        // With a gateway the trust grains live in a separate cluster reached through a private client (R34);
        // without one (Stage A, tests) they share this silo.
        var gateway = silo.Configuration[$"{Trust.TrustOptions.Key}:{nameof(Trust.TrustOptions.Gateway)}"];
        services.AddOptions<Trust.TrustOptions>().BindConfiguration(Trust.TrustOptions.Key)
            .Validate(trust => { trust.Validate(); return true; }).ValidateOnStart();
        if (string.IsNullOrWhiteSpace(gateway))
        {
            silo.AddTrustCluster();
            services.TryAddSingleton(provider => new Trust.GrainTrustClients(provider.GetRequiredService<IGrainFactory>(),
                provider.GetRequiredService<Sequencer.MachineIdentity>(), provider.GetRequiredService<TimeProvider>()));
        }
        else
        {
            services.TryAddSingleton<Trust.TrustGatewayClient>();
            services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(provider => provider.GetRequiredService<Trust.TrustGatewayClient>());
            services.TryAddSingleton(provider =>
            {
                var client = provider.GetRequiredService<Trust.TrustGatewayClient>();
                return new Trust.GrainTrustClients(() => client.Grains, provider.GetRequiredService<Sequencer.MachineIdentity>(), provider.GetRequiredService<TimeProvider>());
            });
            // The trust grain classes ride in this process's assemblies; this silo never hosts them.
            silo.Configure<Orleans.Configuration.GrainTypeOptions>(options =>
                options.Classes.RemoveWhere(type => type.Assembly == typeof(TrustHosting).Assembly));
        }
        services.TryAddSingleton<Trust.ISequencerClient>(provider => provider.GetRequiredService<Trust.GrainTrustClients>());
        services.TryAddSingleton<Trust.INodeDirectoryClient>(provider => provider.GetRequiredService<Trust.GrainTrustClients>());
        services.TryAddSingleton<Trust.IKeyringClient>(provider => provider.GetRequiredService<Trust.GrainTrustClients>());
        services.TryAddSingleton<DigitalBrain.Framework.Platform.Marketplace.IMarketplaceClient>(provider => provider.GetRequiredService<Trust.GrainTrustClients>());
        services.TryAddSingleton<IIdentityClient>(provider => provider.GetRequiredService<Trust.GrainTrustClients>());
        services.TryAddSingleton<Trust.MachineRegistrar>();
        services.TryAddSingleton<BrainBirths>();
        services.TryAddSingleton<PrincipalPasswords>();
        silo.AddStartupTask<Trust.MachineRegistration>();
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
        endpoints.MapGrpcService<Neurons.NeuronsService>();
        return endpoints;
    }

    private sealed class PlatformRegistration;
}
