using DigitalBrain;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Tools;
using DigitalBrain.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace DigitalBrain.Apps;

public sealed class AppsModule() : Module<AppsOptions>(new ModuleId("apps")), IHttpModule
{
    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.AddOptions<AppsOptions>()
            .Configure<IConfiguration>((options, configuration) => configuration.PopulateModuleOptions("apps", options));
        silo.Services.TryAddSingleton(TimeProvider.System);
        silo.AddIncomingGrainCallFilter<MarketplaceWriteGuard>();
        silo.Services.TryAddSingleton<AppInstaller>();
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<DigitalBrain.Framework.IRegistryResourceProvider, AppsRegistryResources>());
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<DigitalBrain.Framework.IRegistryInventoryProvider, AppsInventoryProvider>());
        silo.Services.TryAddSingleton<ITestScriptRunner, CSharpFileTestRunner>();
        silo.Services.TryAddSingleton<AppWorkQueue>();
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolSource, AppAgentTools>());
        silo.Services.TryAddSingleton<AppAuthoringPolicy>();
        silo.Services.TryAddSingleton<Creator>();
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IDeployableRuntime, CSharpAppRuntime>());
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
