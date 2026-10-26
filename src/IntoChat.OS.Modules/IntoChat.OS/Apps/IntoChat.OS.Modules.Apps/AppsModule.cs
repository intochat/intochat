using IntoChat.OS;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace IntoChat.OS.Modules.Apps;

public sealed class AppsModule() : Module<AppsOptions>(new ModuleId("apps")), IHttpModule
{
    // R4-04: what the module needs from its host: its options section (AppsNeurons carries it).
    public static DigitalBrain.ModuleManifest Manifest => AppsNeurons.Manifest;

    public override System.Reflection.Assembly? Contracts => typeof(IntoChat.OS.Modules.Apps.IApp).Assembly;

    // 06-08: the module's plain neurons (apps, the feature reconciler, the studio, the store); the marketplace is still a grain.
    public override Type? Neurons => typeof(AppsNeurons);

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.TryAddSingleton(TimeProvider.System);
        silo.AddIncomingGrainCallFilter<MarketplaceWriteGuard>();
        silo.Services.TryAddSingleton(FeatureCatalog.From);
        silo.Services.TryAddSingleton<FeatureLoader>();
        silo.Services.TryAddSingleton<MarketplaceView>();
        silo.Services.TryAddSingleton<ICapabilitySatisfaction, ConnectedModuleSatisfaction>();
        silo.Services.TryAddSingleton<AppInstaller>();
        silo.Services.TryAddSingleton<AppWorkQueue>();
        silo.Services.TryAddSingleton<AppAuthoringPolicy>();
        silo.Services.TryAddSingleton<Creator>();
        silo.Services.TryAddSingleton(ReactionExamples.Create);
        silo.Services.TryAddSingleton<ReactionWriter>();
        silo.AddStartupTask<FeatureBoot>();
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAppRuntime, CSharpAppRuntime>());
    }

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/session/capabilities", static (IServiceProvider services) =>
            Results.Ok(new { developerMode = services.GetService<IScriptSandbox>()?.CanRun == true }));
        AppsEndpoints.Map(endpoints);
        MarketplaceEndpoints.Map(endpoints);
    }
}

// The DigitalBrain module of the Apps module's plain neurons (06-08).
[DigitalBrain.Module("apps")]
public sealed class AppsNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Setting.Of<AppsOptions>(IntoChatOSConfiguration.ModuleOptionsSection(new ModuleId("apps")))],
        Secrets: []);
}
