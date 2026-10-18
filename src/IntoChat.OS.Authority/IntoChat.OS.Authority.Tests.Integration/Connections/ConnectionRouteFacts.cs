using System.Text.Json;
using IntoChat.OS.Framework.Platform.Connections;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Tests.Integration.Connections;

public sealed class ConnectionRouteFacts
{
    [Fact]
    public async Task ConnectionsAndCapabilitiesAreServedOnlyUnderTheBrainScope()
    {
        var builder = WebApplication.CreateBuilder();
        IntoChat.OS.Testing.TestLogging.Apply(builder.Configuration);
        builder.Services.AddSingleton(typeof(AuthorityNeurons), _ => null!);
        builder.Services.AddSingleton(typeof(IIntoChatOS), _ => null!);
        builder.Services.AddGrpc(); // the platform maps the Neurons channel beside its routes
        await using var web = builder.Build();
        IEndpointRouteBuilder app = web;
        PlatformHosting.MapIntoChatOSPlatform(app);

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

    [Fact]
    public void ARowReachesTheShellWithItsStatusAsAName()
    {
        var row = new ConnectionView("gmail", "mail", "google-oauth", ConnectionStatus.Connected, "a@b.c", null, null, null);

        var json = JsonSerializer.SerializeToElement(row, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(JsonValueKind.String, json.GetProperty("status").ValueKind);
        Assert.Equal("Connected", json.GetProperty("status").GetString());
        Assert.Equal("mail", json.GetProperty("connectionId").GetString());
    }
}
