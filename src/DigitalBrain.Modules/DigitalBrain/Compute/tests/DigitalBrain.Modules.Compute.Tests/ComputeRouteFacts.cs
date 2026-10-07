using DigitalBrain;
using DigitalBrain.Compute;
using DigitalBrain.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Compute.Tests;

public sealed class ComputeRouteFacts
{
    [Fact]
    public void ComputeSummaryAndLimitsAreAccountRoutesOutsideTheBrainGroup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(typeof(IDigitalBrain), _ => null!);
        IEndpointRouteBuilder app = builder.Build();
        new ComputeModule().Configure(app);

        var routes = app.DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText!).ToArray();

        Assert.Equal(["/brains/{brainId}/compute/usage", "/compute/limits", "/compute/summary"], routes.Order(StringComparer.Ordinal).ToArray());
    }
}
