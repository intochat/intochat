using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Framework.Tests.Integration.Enforcement;

public sealed class BrainRoutesFacts(FrameworkHost host)
{
    [Fact]
    public async Task AnInvalidBrainIdIsRejectedBeforeAnyGrainCall()
    {
        var http = new DefaultHttpContext();
        http.Request.RouteValues["brainId"] = "..%2Fescape/";

        var result = await BrainRoutes.ValidateId(http);

        Assert.Equal(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result!).StatusCode);
    }

    [Fact]
    public async Task AGroupedEndpointAnswersAMemberAndForbidsAnyoneElse()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var alice = await host.PersonAsync("alice", ct);
        var bob = await host.PersonAsync("bob", ct);

        using var own = await alice.Http.GetAsync($"/brains/{alice.BrainId}/connections", ct);
        using var foreign = await alice.Http.GetAsync($"/brains/{bob.BrainId}/connections", ct);

        Assert.Equal(System.Net.HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, foreign.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AGroupedEndpointRejectsARequestWithoutAStamp(bool authorized)
    {
        var (status, handlerRan) = await Invoke("personal", authorized);

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(handlerRan);
    }

    [Fact]
    public async Task AGroupedEndpointRejectsAnInvalidBrainIdBeforeMembershipIsChecked()
    {
        var (status, handlerRan) = await Invoke("..%2Fescape/", authorized: true);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.False(handlerRan);
    }

    private static async Task<(int Status, bool HandlerRan)> Invoke(string brainId, bool authorized)
    {
        var builder = WebApplication.CreateBuilder();
        IntoChat.OS.Testing.TestLogging.Apply(builder.Configuration);
        builder.Services.AddSingleton<IBrainAccess, DenyEveryone>();
        IEndpointRouteBuilder app = builder.Build();
        var handlerRan = false;
        BrainRoutes.Group(app).MapGet("/ping", () => { handlerRan = true; return Results.Ok(); });
        var endpoint = app.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().Single();

        var http = new DefaultHttpContext { RequestServices = ((IApplicationBuilder)app).ApplicationServices };
        http.Request.RouteValues["brainId"] = brainId;
        if (authorized) { http.Request.Headers.Authorization = "Basic abc"; }
        http.Response.Body = new MemoryStream();
        http.SetEndpoint(endpoint);

        await endpoint.RequestDelegate!(http);
        return (http.Response.StatusCode, handlerRan);
    }

    private sealed class DenyEveryone : IBrainAccess
    {
        public ValueTask<bool> CanAccessAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }
}
