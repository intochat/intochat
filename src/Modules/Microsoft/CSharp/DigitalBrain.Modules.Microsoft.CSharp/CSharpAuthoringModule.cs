using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Enforcement;
using DigitalBrain.Framework.Enforcement;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace DigitalBrain.Microsoft.CSharp;

public sealed class CSharpAuthoringModule() : Module(new ModuleId("csharp-authoring")), IHttpModule
{
    public override void Configure(ISiloBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddCSharpAuthoring();
        builder.Services.TryAddSingleton<CSharpSharing>();
        builder.Services.TryAddScoped(provider => provider.GetRequiredService<CSharpToolService>().ForScope(BrainScope.CurrentId()));
    }

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        CSharpAuthoringEndpoints.Map(endpoints);
    }
}
