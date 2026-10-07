using DigitalBrain;
using DigitalBrain.AI.Agents;
using DigitalBrain.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Configuration;

namespace DigitalBrain.Apps;

public sealed class AppsModule() : Module<AppsOptions>(new ModuleId("apps")), IHttpModule
{
    public override System.Reflection.Assembly? Contracts => typeof(DigitalBrain.Apps.IApp).Assembly;

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.TryAddSingleton(TimeProvider.System);
        silo.AddIncomingGrainCallFilter<MarketplaceWriteGuard>();
        silo.Services.TryAddSingleton<AppInstaller>();
        silo.Services.TryAddSingleton<AppWorkQueue>();
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
