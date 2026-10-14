using System.Text.Json;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Chart;
using DigitalBrain.Flutter.Table;
using DigitalBrain.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Flutter.Tests;

[Collection(FlutterHostCollection.Name)]
public sealed class AppUiRouteFacts(FlutterHostFixture host)
{
    [Fact]
    public void AppUiNodesEventsAndTablesAreServedUnderTheBrainRoute()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(typeof(IDigitalBrain), _ => null!);
        builder.Services.AddSingleton(typeof(IGrainFactory), _ => null!);
        IEndpointRouteBuilder app = builder.Build();
        AppUiEndpoints.Map(app);

        var routes = app.DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Select(endpoint => $"{string.Join(",", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)} {endpoint.RoutePattern.RawText}").Order(StringComparer.Ordinal).ToArray();

        Assert.All(routes, route => Assert.Contains(" /brains/{brainId}/", route));
        Assert.Equal(
        [
            "GET /brains/{brainId}/apps/node",
            "GET /brains/{brainId}/ui/tables/{name}",
            "POST /brains/{brainId}/apps/connect",
            "POST /brains/{brainId}/apps/event",
            "POST /brains/{brainId}/ui/tables/{name}/replace",
        ], routes);
    }

    [Fact]
    public async Task ATableAndAChartUnderAppsAreReadableAsAppNodes()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        using var person = await host.PersonAsync(ct);
        var ws = person.BrainId;
        using var caller = person.AsCaller();
        var scope = ws;
        await brain.Get<ITable>(scope + "/apps/dv/grid").Replace("People", [new("c", "Country")], [["us"]]);
        await brain.Get<IChart>(scope + "/apps/dv/pie").Render("Share", "pie", [new("1", "us", 2)]);

        var table = await person.Http.GetFromJsonAsync<JsonElement>($"/brains/{ws}/apps/node?kind=table&name={Uri.EscapeDataString(scope + "/apps/dv/grid")}", ct);
        var chart = await person.Http.GetFromJsonAsync<JsonElement>($"/brains/{ws}/apps/node?kind=chart&name={Uri.EscapeDataString(scope + "/apps/dv/pie")}", ct);

        Assert.Equal("People", table.GetProperty("title").GetString());
        Assert.Equal("pie", chart.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task UiEndpointErrorsKeepTheirStatusAndProblemDetail()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        using var person = await host.PersonAsync(ct);
        var ws = person.BrainId;
        using var caller = person.AsCaller();
        var scope = ws;

        var foreign = await person.Http.GetAsync($"/brains/{ws}/apps/node?kind=text&name={Uri.EscapeDataString("other-brain/apps/dv/x")}", ct);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, foreign.StatusCode);
        var detail = await foreign.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(detail.TryGetProperty("detail", out _));

        var unknown = await person.Http.GetAsync($"/brains/{ws}/apps/node?kind=nope&name={Uri.EscapeDataString(scope + "/apps/dv/x")}", ct);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, unknown.StatusCode);
    }
}
