using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace DigitalBrain.Microsoft.Aspire;

public sealed class AspireModule() : Module<AspireOptions>(new ModuleId("aspire")), IHttpModule
{
    public override System.Reflection.Assembly? Contracts => typeof(DigitalBrain.Microsoft.Aspire.IAspire).Assembly;

    public static readonly string ConfigurationRoot = AspireOptions.ConfigurationRoot;

    public override void Configure(ISiloBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<AspireBridge>();
    }

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapAspireBridge();
    }
}
