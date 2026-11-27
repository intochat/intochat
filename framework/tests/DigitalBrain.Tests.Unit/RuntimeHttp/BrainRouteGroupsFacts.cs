using System.Net;
using System.Security.Claims;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Testing.Conformance.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Runtime.Http.Tests.Unit;

public sealed class BrainRouteGroupsFacts
{
    private const string TestPrincipalHeader = "X-Test-Principal";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Brain_group_marks_GuestBrainDoor_and_Session_does_not()
    {
        await using var host = await TestHost.Start(
            _ => { },
            app =>
            {
                BrainRouteGroups.Brain(app, "/open").MapGet("/ping", () => Results.Ok());
                BrainRouteGroups.Brain(app, "/closed").WithMetadata(BrainRouteGroups.GuestClosed.Instance).MapGet("/ping", () => Results.Ok());
                BrainRouteGroups.Session(app, "/session").MapGet("/ping", () => Results.Ok());
            });

        var endpoints = host.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        var open = Assert.Single(endpoints, e => e.RoutePattern.RawText == "/brains/{brainId}/open/ping");
        var closed = Assert.Single(endpoints, e => e.RoutePattern.RawText == "/brains/{brainId}/closed/ping");
        var session = Assert.Single(endpoints, e => e.RoutePattern.RawText == "/brains/{brainId}/session/ping");

        Assert.NotNull(open.Metadata.GetMetadata<BrainRouteGroups.GuestBrainDoor>());
        Assert.Null(open.Metadata.GetMetadata<BrainRouteGroups.GuestClosed>());
        Assert.NotNull(closed.Metadata.GetMetadata<BrainRouteGroups.GuestBrainDoor>());
        Assert.NotNull(closed.Metadata.GetMetadata<BrainRouteGroups.GuestClosed>());
        Assert.Null(session.Metadata.GetMetadata<BrainRouteGroups.GuestBrainDoor>());
    }

    [Fact]
    public async Task Brain_access_override_controls_admission_and_invokes_initializer_only_when_admitted()
    {
        var access = new StubBrainAccess();
        var initializer = new RecordingInitializer();
        await using var host = await TestHost.Start(
            services =>
            {
                services.AddSingleton<IBrainAccess>(access);
                services.AddSingleton<IBrainRouteInitializer>(initializer);
            },
            app => BrainRouteGroups.Brain(app, "/items").MapGet("", () => Results.Ok("ok")));

        // No caller -> 401, initializer not invoked.
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Send(HttpMethod.Get, "/brains/b1/items", null, Ct)).StatusCode);
        Assert.Empty(initializer.Calls);

        // Denied by IBrainAccess -> 403, initializer not invoked.
        access.Allow = false;
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Send(HttpMethod.Get, "/brains/b1/items", "ada", Ct)).StatusCode);
        Assert.Empty(initializer.Calls);

        // Admitted by IBrainAccess -> 200, initializer invoked with (accountId, brainId).
        access.Allow = true;
        Assert.Equal(HttpStatusCode.OK, (await host.Send(HttpMethod.Get, "/brains/b1/items", "ada", Ct)).StatusCode);
        Assert.Equal([("ada", "b1")], initializer.Calls);
    }

    [Fact]
    public async Task Decide_supports_ambient_caller_and_denies_caller_acting_for_nobody()
    {
        var edge = EdgeAuthority.CreateInternal();
        var ambient = new MutableAmbient();
        var access = new StubBrainAccess { Allow = true };
        var initializer = new RecordingInitializer();
        var services = new ServiceCollection()
            .AddSingleton<ICallerAmbient>(ambient)
            .AddSingleton<IBrainAccess>(access)
            .AddSingleton<IBrainRouteInitializer>(initializer)
            .BuildServiceProvider();

        var http = new DefaultHttpContext { RequestServices = services };

        // No ambient caller and no http.User -> 401.
        var unauth = await BrainRouteGroups.Decide(http, "b1");
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsAssignableFrom<IStatusCodeHttpResult>(unauth).StatusCode);

        // Scheduler caller with For == null -> 403.
        ambient.Current = edge.Scheduler(new BrainId("b1"));
        var denied = await BrainRouteGroups.Decide(http, "b1");
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<IStatusCodeHttpResult>(denied).StatusCode);

        // Person caller -> admitted, initializer called with account & brain.
        ambient.Current = edge.Person("ada", "acct-ada", new BrainId("b1"));
        var admitted = await BrainRouteGroups.Decide(http, "b1");
        Assert.Null(admitted);

        Assert.Equal([("acct-ada", "b1")], initializer.Calls);
    }

    [Fact]
    public async Task Decide_falls_back_to_fail_closed_identity_membership_and_invokes_initializer()
    {
        var identity = new InMemoryIdentity();
        var ada = (await identity.RegisterPrincipal("ada", "Ada", Ct)).Member;
        var initializer = new RecordingInitializer();
        var services = new ServiceCollection()
            .AddKeyedSingleton<IIdentity>(Capability.Identity.V1Id, identity)
            .AddSingleton<IBrainRouteInitializer>(initializer)
            .BuildServiceProvider();

        var http = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(SessionClaims.Principal, "ada")], "test")),
        };

        var admitted = await BrainRouteGroups.Decide(http, ada.BrainId);
        Assert.Null(admitted);
        Assert.Equal("ada", BrainRouteGroups.MemberOf(http)?.PrincipalId);
        Assert.Equal([(ada.AccountId, ada.BrainId)], initializer.Calls);
    }

    [Fact]
    public async Task Decide_fails_closed_when_neither_access_nor_identity_is_registered()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var http = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(SessionClaims.Principal, "ada")], "test")),
        };

        var denied = await BrainRouteGroups.Decide(http, "b1");
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<IStatusCodeHttpResult>(denied).StatusCode);
    }

    private sealed class MutableAmbient : ICallerAmbient
    {
        public Caller? Current { get; set; }
    }

    private sealed class StubBrainAccess : IBrainAccess
    {
        public bool Allow { get; set; }

        public ValueTask<bool> CanAccessAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Allow);
    }

    private sealed class RecordingInitializer : IBrainRouteInitializer
    {
        public List<(string AccountId, string BrainId)> Calls { get; } = [];

        public ValueTask InitializeAsync(string accountId, string brainId, CancellationToken cancellationToken = default)
        {
            Calls.Add((accountId, brainId));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;

        public static async Task<TestHost> Start(Action<IServiceCollection> configure, Action<WebApplication> map)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            configure(builder.Services);
            var app = builder.Build();
            app.Use((http, next) =>
            {
                if (http.Request.Headers[TestPrincipalHeader].ToString() is { Length: > 0 } principal)
                {
                    http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(SessionClaims.Principal, principal)], "test"));
                }

                return next(http);
            });
            map(app);
            await app.StartAsync(Ct);
            return new TestHost(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
        }

        public async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? principal, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(method, path);
            if (principal is not null) { request.Headers.Add(TestPrincipalHeader, principal); }
            return await client.SendAsync(request, ct);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
