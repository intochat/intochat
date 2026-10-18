using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Authority.Tests.Integration.Neurons;

public sealed class NeuronsRouteFacts
{
    [Fact]
    public async Task TheNeuronChannelIsMappedBesideThePlatformRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        DigitalBrain.Testing.TestLogging.Apply(builder.Configuration);
        builder.Services.AddSingleton(typeof(IGrainFactory), _ => null!);
        builder.Services.AddSingleton(typeof(IDigitalBrain), _ => null!);
        builder.Services.AddGrpc();
        await using var web = builder.Build();
        IEndpointRouteBuilder app = web;
        PlatformHosting.MapDigitalBrainPlatform(app);

        var routes = app.DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText!).ToArray();

        Assert.Contains("/digitalbrain.Neurons/Call", routes);
        Assert.Contains("/digitalbrain.Neurons/Watch", routes);
    }
}
