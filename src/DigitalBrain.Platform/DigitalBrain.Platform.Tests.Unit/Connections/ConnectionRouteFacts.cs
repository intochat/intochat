using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Connections;

public sealed class ConnectionRouteFacts
{
    [Fact]
    public async Task ConnectionsAndCapabilitiesAreServedOnlyUnderTheBrainScope()
    {
        var builder = WebApplication.CreateBuilder();
        DigitalBrain.Testing.TestLogging.Apply(builder.Configuration);
        builder.Services.AddSingleton(typeof(IGrainFactory), _ => null!);
        builder.Services.AddSingleton(typeof(IDigitalBrain), _ => null!);
        await using var web = builder.Build();
        IEndpointRouteBuilder app = web;
        PlatformHosting.MapDigitalBrainPlatform(app);

        var routes = app.DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText!.TrimEnd('/')).ToArray();

        Assert.Contains("/brains/{brainId}/integrations/capabilities", routes);
        Assert.Contains("/brains/{brainId}/connections", routes);
        Assert.Contains("/brains/{brainId}/connections/requests", routes);
        Assert.Contains("/brains/{brainId}/connections/{connectionId}/connect", routes);
        Assert.Contains("/brains/{brainId}/connections/{connectionId}/login", routes);
        Assert.Contains("/brains/{brainId}/connections/{connectionId}/probe", routes);
        Assert.Contains("/brains/{brainId}/connections/{connectionId}/disconnect", routes);
        // The OAuth callback is Platform middleware, not a route; no connection route escapes the brain.
        Assert.DoesNotContain(routes, route => route.StartsWith("/connections", StringComparison.Ordinal));
    }
}
