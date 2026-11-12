global using DigitalBrain.Brains;
global using DigitalBrain.Neurons;
global using DigitalBrain.Signals;
global using CallerContextStamper = DigitalBrain.Runtime.Orleans.Capabilities.RuntimeCallerContext;
using System.Reflection;
using DigitalBrain.Contracts;
using DigitalBrain.Modules;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Modules.Options;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.AI.Agents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Modules.Module("apps")]
public sealed class AppsModule : DigitalBrain.Modules.Module, IHttpModule
{
    // R4-04: what the module needs from its host: its options section.
    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Modules.Setting.Of<AppsOptions>(ModuleConfiguration.ModuleOptionsSection(new ModuleId("apps")))],
        Secrets: []);

    public static System.Reflection.Assembly? Contracts { get; } = typeof(IntoChat.OS.Modules.Apps.IApp).Assembly;

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IIncomingGrainCallFilter, MarketplaceWriteGuard>();
        services.AddSingleton(new CompiledAppsRegistration(typeof(AppsModule).Assembly));
        services.TryAddSingleton(FeatureCatalog.From);
        services.TryAddSingleton<FeatureLoader>();
        services.TryAddSingleton<MarketplaceView>();
        services.TryAddSingleton<ICapabilitySatisfaction, ConnectedModuleSatisfaction>();
        services.TryAddSingleton<AppInstaller>();

        services.TryAddSingleton<IDrafter>(sp => sp.GetRequiredService<Creator>());
        services.TryAddSingleton<Creator>();
        services.AddNeuronProxies();
        services.TryAddSingleton<IPolicyModel, CreatorPolicyModel>();
        services.TryAddSingleton<PolicyWriter>();
        // FeatureBoot runs once the silo is active, as an Orleans startup task did.
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, FeatureBootLifecycle>();
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, FilesLifecycle>();
        services.AddSingleton<DigitalBrain.Graph.IGraphSource, AppGraphSource>();
        services.AddSingleton<DigitalBrain.Graph.IGraphSource, Flows.FlowGraphSource>();
    }

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        AppsEndpoints.Map(endpoints);
        ArtCraftWebEndpoints.Map(endpoints);
        PoliciesEndpoints.Map(endpoints);
        MarketplaceEndpoints.Map(endpoints);
    }
}
