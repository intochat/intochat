using DigitalBrain;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Flutter.Space;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace DigitalBrain.Flutter;

public sealed class FlutterModule() : Module<FlutterModuleOptions>(new ModuleId("flutter")), IHttpModule
{
    public override System.Reflection.Assembly? Contracts => typeof(DigitalBrain.Flutter.Button.IButton).Assembly;

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.AddHostedService<SpaceWatcher>();
    }

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapShellPersistence();
        WorkspaceEndpoints.Map(endpoints);
        LiveSignalEndpoints.Map(endpoints);
        BrainEventsEndpoints.Map(endpoints);
        AppUiEndpoints.Map(endpoints);
    }
}
