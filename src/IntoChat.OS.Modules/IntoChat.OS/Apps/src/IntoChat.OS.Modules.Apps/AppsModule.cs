using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.Modules.AI.Agents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Module("apps")]
public sealed class AppsModule : DigitalBrain.Module, IHttpModule
{
    // R4-04: what the module needs from its host: its options section.
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Setting.Of<AppsOptions>(IntoChatOSConfiguration.ModuleOptionsSection(new ModuleId("apps")))],
        Secrets: []);

    public static System.Reflection.Assembly? Contracts { get; } = typeof(IntoChat.OS.Modules.Apps.IApp).Assembly;

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IIncomingGrainCallFilter, MarketplaceWriteGuard>();
        services.TryAddSingleton<GitSideload>();
        services.TryAddSingleton(FeatureCatalog.From);
        services.TryAddSingleton<FeatureLoader>();
        services.TryAddSingleton<MarketplaceView>();
        services.TryAddSingleton<ICapabilitySatisfaction, ConnectedModuleSatisfaction>();
        services.TryAddSingleton<AppInstaller>();
        services.TryAddSingleton<SideloadInstaller>();
        services.TryAddSingleton<AppWorkQueue>();
        services.TryAddSingleton<AppAuthoringPolicy>();
        services.TryAddSingleton<Creator>();
        services.TryAddSingleton(ReactionExamples.Create);
        services.TryAddSingleton<ReactionWriter>();
        // FeatureBoot runs once the silo is active, as an Orleans startup task did.
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, FeatureBootLifecycle>();
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, BrainSceneLifecycle>();
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, FilesLifecycle>();
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, CompiledAppsLifecycle>();
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, SettingsModulesLifecycle>();
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, SettingsSideloadLifecycle>();
        services.AddSingleton<DevAppGate>();
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, DevTokenLifecycle>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAppRuntime, CSharpAppRuntime>());
    }

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/session/capabilities", static (IServiceProvider services) =>
            Results.Ok(new { developerMode = services.GetService<IScriptSandbox>()?.CanRun == true }));
        AppsEndpoints.Map(endpoints);
        MarketplaceEndpoints.Map(endpoints);
        DevAppEndpoints.Map(endpoints);
    }
}
