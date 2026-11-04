global using DigitalBrain.Brains;
global using DigitalBrain.Signals;
global using CallerContextStamper = DigitalBrain.Runtime.Orleans.Capabilities.RuntimeCallerContext;

using DigitalBrain.Modules.Configuration;
using DigitalBrain.Modules.Options;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Runtime.Orleans.Capabilities;
using System.Reflection;
using DigitalBrain.Apps.Hosting;
using DigitalBrain.Contracts;
using DigitalBrain.Runtime.Apps;
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

[DigitalBrain.Module("apps")]
public sealed class AppsModule : DigitalBrain.Module, IHttpModule
{
    // R4-04: what the module needs from its host: its options section.
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Setting.Of<AppsOptions>(ModuleConfiguration.ModuleOptionsSection(new ModuleId("apps")))],
        Secrets: []);

    public static System.Reflection.Assembly? Contracts { get; } = typeof(IntoChat.OS.Modules.Apps.IApp).Assembly;

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IIncomingGrainCallFilter, MarketplaceWriteGuard>();
        services.TryAddSingleton<GitSideload>();
        services.AddSingleton(new CompiledAppsRegistration(typeof(AppsModule).Assembly));
        services.TryAddSingleton(FeatureCatalog.From);
        services.TryAddSingleton<FeatureLoader>();
        services.TryAddSingleton<MarketplaceView>();
        services.TryAddSingleton<ICapabilitySatisfaction, ConnectedModuleSatisfaction>();
        services.TryAddSingleton<AppInstaller>();
        services.TryAddSingleton<SideloadInstaller>();
        services.TryAddSingleton<AppPresentationSink>();
        services.TryAddSingleton<IPresentationSink>(sp => sp.GetRequiredService<AppPresentationSink>());
        services.TryAddSingleton<InProcessAppHost>(sp => new InProcessAppHost(
            neuronResolver: (contractType, neuronKey) => ResolveNeuron(sp, contractType, neuronKey),
            capabilityResolver: capType => sp.GetService(capType)
                ?? throw new InvalidOperationException($"Capability '{capType.Name}' is not registered."),
            sink: sp.GetRequiredService<IPresentationSink>(),
            services: serviceType => sp.GetService(serviceType)));
        services.TryAddSingleton<IAppHost>(sp => sp.GetRequiredService<InProcessAppHost>());
        services.TryAddSingleton<AppWorkQueue>();
        services.TryAddSingleton<AppAuthoringPolicy>();
        services.TryAddSingleton<Creator>();
        services.TryAddSingleton(ReactionExamples.Create);
        services.TryAddSingleton<ReactionWriter>();
        services.AddNeuronProxies();
        services.TryAddSingleton<IPolicyModel, CreatorPolicyModel>();
        services.TryAddSingleton<PolicyWriter>();
        // FeatureBoot runs once the silo is active, as an Orleans startup task did.
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, FeatureBootLifecycle>();
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, BrainSceneLifecycle>();
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, FilesLifecycle>();
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, CompiledAppsLifecycle>();
        services.AddSingleton<DevAppGate>();
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, DevTokenLifecycle>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAppRuntime, InProcessAppRuntime>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAppRuntime, CSharpAppRuntime>());
        services.AddSingleton<DigitalBrain.Graph.IGraphSource, AppGraphSource>();
    }

    private static object ResolveNeuron(IServiceProvider services, Type contractType, string neuronKey)
    {
        var brain = RuntimeCallerContext.TryGet(out var caller) && !string.IsNullOrWhiteSpace(caller.BrainId)
            ? caller.BrainId
            : DigitalBrain.Runtime.Orleans.Capabilities.RuntimeCallerContext.Current is { BrainId: { Length: > 0 } b }
                ? b
                : "owner";
        var fullKey = neuronKey.Contains('/', StringComparison.Ordinal) && string.Equals(BrainKey.BrainOf(neuronKey), brain, StringComparison.Ordinal)
            ? neuronKey
            : $"{brain}/{neuronKey}";
        if (contractType.GetCustomAttribute<DefaultNeuronTypeAttribute>(inherit: false) is not null)
        {
            return SiloNeurons.Get(services, contractType, fullKey);
        }

        var alias = contractType.GetCustomAttribute<NeuronAliasAttribute>(inherit: false)?.Alias
            ?? contractType.GetCustomAttribute<DigitalBrain.NeuronAttribute>(inherit: false)?.Type
            ?? throw new InvalidOperationException($"Contract {contractType.Name} has neither DefaultNeuronType nor NeuronAlias.");
        var sep = fullKey.IndexOf('/', StringComparison.Ordinal);
        var id = new DigitalBrain.NeuronId(new DigitalBrain.BrainId(fullKey[..sep]), alias, fullKey[(sep + 1)..]);
        return SiloNeurons.Get(services, contractType, id);
    }

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/session/capabilities", static (IServiceProvider services) =>
            Results.Ok(new { developerMode = services.GetService<IScriptSandbox>()?.CanRun == true }));
        AppsEndpoints.Map(endpoints);
        PoliciesEndpoints.Map(endpoints);
        MarketplaceEndpoints.Map(endpoints);
        DevAppEndpoints.Map(endpoints);
    }
}
