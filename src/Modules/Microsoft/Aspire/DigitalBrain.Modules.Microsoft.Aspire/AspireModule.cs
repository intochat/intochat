using DigitalBrain;
using DigitalBrain.Kernel;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace DigitalBrain.Microsoft.Aspire;

public sealed class AspireModule() : Module<AspireOptions>(new ModuleId("aspire")), IHttpModule
{
    public static readonly string ConfigurationRoot = AspireOptions.ConfigurationRoot;

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
