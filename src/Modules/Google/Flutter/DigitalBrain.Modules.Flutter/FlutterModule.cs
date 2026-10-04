using DigitalBrain.Contracts;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Kernel;
using DigitalBrain.Kernel.AspNetCore;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace DigitalBrain.Flutter;

[ModuleHosting("DigitalBrain.Flutter.Aspire.Hosting.FlutterModuleHosting, DigitalBrain.Modules.Flutter.Aspire.Hosting")]
[ModuleId("flutter")]
public sealed class FlutterModule : IModule<FlutterModuleOptions>, IHttpModule
{
    public void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.AddOptions<FlutterModuleOptions>().Configure<IConfiguration>(
            static (options, configuration) => configuration.GetSection("DigitalBrain:Modules:flutter:Options").Bind(options));
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
