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
using Microsoft.Extensions.Options;
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

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IIncomingGrainCallFilter, MarketplaceWriteGuard>();
        services.TryAddSingleton(FeatureCatalog.From);
        services.TryAddSingleton<FeatureLoader>();
        services.TryAddSingleton<MarketplaceView>();
        services.TryAddSingleton<ICapabilitySatisfaction, ConnectedModuleSatisfaction>();
        services.TryAddSingleton<AppInstaller>();
        services.TryAddSingleton<CompiledAppReviewStore>();
        services.TryAddSingleton<CompiledAppInstaller>();
        services.TryAddSingleton<CompiledAppDevelopment>();
        services.TryAddSingleton(configured => new ActiveCompiledApps(
            Path.Combine(DigitalBrain.Runtime.Orleans.StateHome.Resolve(configuration).Apps, "packages", "active")));
        services.TryAddSingleton<ICompiledAppAssignment, SandboxCompiledApps>();
        // The OS publish output carries the prebuilt built-ins under compiled-apps/{publisher}; Features:CompiledFeatures overrides the folder.
        services.TryAddSingleton(provider => new CompiledBuiltinCatalog(
            Path.GetFullPath(provider.GetRequiredService<IOptions<AppsOptions>>().Value.CompiledFeatures is { Length: > 0 } given ? given : "compiled-apps", AppContext.BaseDirectory),
            provider.GetRequiredService<CompiledAppPackages>()));
        services.TryAddSingleton(_ => new CompiledAppPackages(
            Path.Combine(DigitalBrain.Runtime.Orleans.StateHome.Resolve(configuration).Apps, "packages"),
            new[]
            {
                typeof(DigitalBrain.App).Assembly,
                typeof(DigitalBrain.UI.IPresenter).Assembly,
                typeof(DigitalBrain.Capabilities.Storage.IStorage).Assembly,
                typeof(DigitalBrain.Capabilities.Secrets.ISecrets).Assembly,
                typeof(DigitalBrain.Capabilities.Identity.IIdentity).Assembly,
            }.Select(assembly => assembly.GetName()).ToDictionary(name => name.Name!, name => name.Version!.ToString(), StringComparer.Ordinal)));
        services.TryAddSingleton<AppWorkQueue>();
        services.TryAddSingleton<AppAuthoringPolicy>();
        services.TryAddSingleton<Creator>();
        services.TryAddSingleton(ReactionExamples.Create);
        services.TryAddSingleton<ReactionWriter>();
        // FeatureBoot runs once the silo is active, as an Orleans startup task did.
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, FeatureBootLifecycle>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAppRuntime, CSharpAppRuntime>());
    }

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/session/capabilities", static (IServiceProvider services) =>
            Results.Ok(new { developerMode = services.GetService<IScriptSandbox>()?.CanRun == true }));
        AppsEndpoints.Map(endpoints);
        MarketplaceEndpoints.Map(endpoints);
        CompiledAppsEndpoints.Map(endpoints);
        CompiledAppDevelopmentEndpoints.Map(endpoints);
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
