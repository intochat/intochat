using IntoChat.OS.Runtime;
using DigitalBrain.Spi;
using IntoChat.OS;
using IntoChat.OS.Modules.Flutter.Workspace;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Workspace;

public sealed class WorkspaceRouteFacts
{
    [Fact]
    public void NavigationStateAndReportsAreServedOnlyUnderTheBrainRoute()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(typeof(INeuronProxies), _ => null!);
        IEndpointRouteBuilder app = builder.Build();
        WorkspaceEndpoints.Map(app);

        var routes = app.DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Select(endpoint => $"{string.Join(",", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)} {endpoint.RoutePattern.RawText}").Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(
            [
                "GET /brains/{brainId}/",
                "POST /brains/{brainId}/back",
                "POST /brains/{brainId}/close",
                "POST /brains/{brainId}/home",
                "POST /brains/{brainId}/reports",
                "POST /brains/{brainId}/show",
            ],
            routes);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AReportKeepsTheIntentIdAndSurvivesReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var reports = brain.Get<IProblemReports>("workspace-scope-1/main");

        var report = await reports.Add("workspace-1", "run-42", "The table did not open.");
        await brain.DeactivateAsync(reports, ct);

        Assert.Equal("run-42", report.IntentId);
        Assert.Equal(report, Assert.Single(await reports.Read()));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnEmptyIntentIdIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            brain.Get<IProblemReports>("workspace-scope-1/main").Add("workspace-1", "  ", "something broke"));
    }
}
