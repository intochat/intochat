using DigitalBrain.Contracts;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Kernel;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace DigitalBrain.Flutter;

public sealed class FlutterModule() : Module<FlutterModuleOptions>(new ModuleId("flutter")), IHttpModule
{
    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.AddOptions<FlutterModuleOptions>().Configure<IConfiguration>(
            static (options, configuration) => configuration.PopulateModuleOptions("flutter", options));
    }

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapUiKit();
        endpoints.MapShellPersistence();
        WorkspaceEndpoints.Map(endpoints);
        AppUiEndpoints.Map(endpoints);
    }
}
