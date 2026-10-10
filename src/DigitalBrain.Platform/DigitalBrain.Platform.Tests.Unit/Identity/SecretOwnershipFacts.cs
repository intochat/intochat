using System.Text;
using DigitalBrain.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Identity;

public sealed class SecretOwnershipFacts
{
    [Fact]
    public async Task AnotherOwnersSecretIsForbiddenWhateverThePathSays()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddGrpc(); // the platform maps the Neurons channel beside its routes
        builder.Services.AddSingleton(brain.Grains);
        builder.Services.AddSingleton<DigitalBrain.IDigitalBrain>(brain);
        builder.Services.AddSingleton<Orleans.IGrainFactory>(brain.Grains);
        await using var app = builder.Build();
        DigitalBrain.Platform.PlatformHosting.MapDigitalBrainPlatform(app);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Single(route => route.RoutePattern.RawText == "/secrets/{owner}");
        CallerContextStamper.Stamp(new CallerContext
        {
            PrincipalId = "owner",
            AccountId = "account",
            BrainId = "brain",
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        });
        var context = new DefaultHttpContext { RequestServices = app.Services, RequestAborted = ct };
        context.Request.Method = "POST";
        context.Request.RouteValues["owner"] = "someone-else";
        context.Request.ContentType = "application/json";
        var body = Encoding.UTF8.GetBytes("""{"name":"me.apiKey","label":"API key","value":"canary"}""");
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBody());
        await endpoint.RequestDelegate!(context);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    private sealed class RequestBody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}
