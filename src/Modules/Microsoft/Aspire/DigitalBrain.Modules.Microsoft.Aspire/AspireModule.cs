using DigitalBrain.Contracts;
using DigitalBrain.Kernel;
using DigitalBrain.Kernel.AspNetCore;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace DigitalBrain.Microsoft.Aspire;

[ModuleHosting("DigitalBrain.Microsoft.Aspire.AspireModuleHosting, DigitalBrain.Modules.Microsoft.Aspire.Aspire.Hosting")]
public sealed class AspireModule() : Module<AspireOptions>(new ModuleId("aspire")), IHttpModule
{
    public const string ConfigurationRoot = AspireOptions.ConfigurationRoot;

    public override void Configure(ISiloBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOptions<AspireOptions>()
            .Configure<IConfiguration>((options, configuration) => configuration.PopulateModuleOptions("aspire", options));
        builder.Services.TryAddSingleton<AspireBridge>();
    }

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapAspireBridge();
    }
}
