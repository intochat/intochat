using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Tests.Integration.Neurons;

public sealed class NeuronsRouteFacts
{
    [Fact]
    public async Task TheNeuronChannelIsMappedBesideThePlatformRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        IntoChat.OS.Testing.TestLogging.Apply(builder.Configuration);
        builder.Services.AddSingleton(typeof(AuthorityNeurons), _ => null!);
        builder.Services.AddSingleton(typeof(IIntoChatOS), _ => null!);
        builder.Services.AddGrpc();
        await using var web = builder.Build();
        IEndpointRouteBuilder app = web;
        PlatformHosting.MapIntoChatOSPlatform(app);

        var routes = app.DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText!).ToArray();

        Assert.Contains("/intochat.os.Neurons/Call", routes);
        Assert.Contains("/intochat.os.Neurons/Watch", routes);
    }
}
