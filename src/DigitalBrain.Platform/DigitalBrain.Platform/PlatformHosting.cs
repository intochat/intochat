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
using DigitalBrain.Platform.Links;
using DigitalBrain.Platform.Secrets;
using DigitalBrain.Platform.Trust;
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
        services.TryAddSingleton<DigitalBrain.Framework.Parts.IMachineSigningKey>(provider => Sequencer.FileMachineSigningKey.LoadOrCreate(Path.Combine(
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Parts.PartsOptions>>().Value.MachineDirectory, "identity.key")));
        services.TryAddSingleton(provider => new Sequencer.MachineIdentity(provider.GetRequiredService<DigitalBrain.Framework.Parts.IMachineSigningKey>()));
        // R75: the identity key is the machine's; what it remembers of a trust deployment lives in that deployment's folder.
        services.TryAddSingleton<Trust.TrustDeployment>();
        services.TryAddSingleton(provider => new Sequencer.SeenVersions(() => Path.Combine(
            provider.GetRequiredService<Trust.TrustDeployment>().Directory, "seen.json")));
        // The custody root is the same configured master key the owner-key wrapper derives from.
        services.TryAddSingleton<DigitalBrain.Framework.Parts.IMasterKeyCustody>(provider => new Keys.ConfiguredMasterKeyCustody(
            System.Text.Encoding.UTF8.GetBytes(provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MasterKeyOptions>>().Value.MasterKey)));
        services.TryAddSingleton<DigitalBrain.Framework.Parts.IPartKeys, Keys.PartKeys>();
        // The machine reaches the trust grains only through these clients; Stage A hosts them in this silo.
        services.TryAddSingleton(provider => new Trust.MachineCounter(() => Path.Combine(
            provider.GetRequiredService<Trust.TrustDeployment>().Directory, "counter")));
        // With an Endpoint the trust grains live in a separate cluster reached over this machine's trust link (T20-7);
        // without one (Stage A, tests) they share this silo.
        if (string.IsNullOrWhiteSpace(silo.Configuration[$"{Trust.TrustOptions.Key}:{nameof(Trust.TrustOptions.Endpoint)}"]))
        { silo.AddTrustCluster(); }
        else
        {
            silo.AddMachineLinks();
            // The trust grain classes ride in this process's assemblies; this silo never hosts them.
            silo.Configure<Orleans.Configuration.GrainTypeOptions>(options =>
                options.Classes.RemoveWhere(type => type.Assembly == typeof(TrustHosting).Assembly));
        }
        services.AddTrustClients(signs: true);
        services.TryAddSingleton<Trust.MachineRegistrar>();
        services.TryAddSingleton<BrainBirths>();
        services.TryAddSingleton<PrincipalPasswords>();
        silo.AddStartupTask<Trust.MachineRegistration>(Trust.MachineRegistration.Stage);
        services.TryAddSingleton<Trust.OwnerBirth>();
        silo.AddStartupTask((provider, ct) => provider.GetRequiredService<Trust.OwnerBirth>().Execute(ct), Trust.OwnerBirth.Stage);
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
        Parts.MachinePartEndpoints.Map(endpoints);
        endpoints.MapGrpcService<Neurons.NeuronsService>();
        return endpoints;
    }

    private sealed class PlatformRegistration;
}
