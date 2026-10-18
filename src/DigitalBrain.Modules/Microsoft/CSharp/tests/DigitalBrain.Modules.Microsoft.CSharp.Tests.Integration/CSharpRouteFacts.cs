using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Authoring;
using DigitalBrain.Microsoft.CSharp.Edge;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests.Integration;

public sealed class CSharpRouteFacts
{
    [Fact]
    public void CSharpAuthoringIsServedOnlyUnderTheBrainRoute()
    {
        var (_, endpoints) = MapAuthoringRoutes();
        var routes = endpoints.Where(endpoint => endpoint.RoutePattern.RawText!.Contains("/csharp", StringComparison.Ordinal)).Select(endpoint => $"{string.Join(",", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)} {endpoint.RoutePattern.RawText}").Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(
            [
                "DELETE /brains/{brainId}/csharp/{id}",
                "GET /brains/{brainId}/csharp/",
                "GET /brains/{brainId}/csharp/{id}",
                "POST /brains/{brainId}/csharp/{id}/start",
                "POST /brains/{brainId}/csharp/{id}/stop",
                "PUT /brains/{brainId}/csharp/{id}",
            ],
            routes);
    }

    [Fact]
    public void ExecutionCompositionDoesNotMapCSharpAuthoringRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        DigitalBrain.Testing.TestLogging.Apply(builder.Configuration);
        builder.Services.AddSingleton<DigitalBrain.Microsoft.CSharp.Edge.RuntimeEdge>(_ => null!);
        builder.Services.AddSingleton(new ScopedCSharpTools(null!, null!, null, "scope", canRun: false));
        builder.Services.Configure<CSharpOptions>(options => options.Authoring = false);
        var app = builder.Build();
        new CSharpModule().Configure(app);
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>();
        Assert.DoesNotContain(routes, route => route.RoutePattern.RawText!.Contains("/csharp", StringComparison.Ordinal));
    }

    private static (WebApplication App, RouteEndpoint[] Endpoints) MapAuthoringRoutes(params (string Key, string Value)[] settings)
    {
        var builder = WebApplication.CreateBuilder();
        DigitalBrain.Testing.TestLogging.Apply(builder.Configuration);
        builder.Configuration.AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)));
        builder.Services.AddSingleton<DigitalBrain.Microsoft.CSharp.Edge.RuntimeEdge>(_ => null!);
        builder.Services.AddSingleton(new ScopedCSharpTools(null!, null!, null, "scope", canRun: false));
        var app = builder.Build();
        new CSharpModule().Configure(app);
        return (app, ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray());
    }
}
