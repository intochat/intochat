using DigitalBrain.Contracts;
using DigitalBrain.Kernel;
using DigitalBrain.Kernel.AspNetCore;
using DigitalBrain.Kernel.Enforcement;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace DigitalBrain.Microsoft.CSharp;

[ModuleHosting("DigitalBrain.Microsoft.CSharp.CSharpAuthoringModuleHosting, DigitalBrain.Modules.Microsoft.CSharp.Aspire.Hosting")]
[ModuleId("csharp-authoring")]
public sealed class CSharpAuthoringModule : IModule, IHttpModule
{
    public void Configure(ISiloBuilder builder)
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
