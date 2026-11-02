using System.Net;
using DigitalBrain.Capabilities;
using IntoChat.OS.Authority.Identity.Handles;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Http;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Authority;

public sealed class HandleRoutesFacts
{
    [Theory]
    [InlineData("apps", "photocraft", "/brains/b1/apps/photocraft")]
    [InlineData("files", "notes.md", "/brains/b1/storage/notes.md")]
    [InlineData("posts", "1", null)]
    public void RewriteMapsKnownCollections(string collection, string key, string? expected)
        => Assert.Equal(expected, HandleRoutes.Rewrite("vlad", collection, key, "b1"));

    [Fact]
    public async Task UnknownAndForbiddenHandlesAreIndistinguishable()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = typeof(HandleRoutesFacts).Assembly.GetName().Name,
            ContentRootPath = Path.GetTempPath(),
        });
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var access = new TestBrainAccess();
        builder.Services.AddSingleton<IBrainAccess>(access);

        var directory = new Dictionary<string, string?>
        {
            ["vlad"] = "b1",
            ["nobody"] = null,
        };
        builder.Services.AddSingleton<Func<string, IHandleDirectory>>(h =>
            directory.TryGetValue(h, out var brainId)
                ? new FakeHandleDirectory(brainId)
                : new FakeHandleDirectory(null));

        var app = builder.Build();

        app.Use(async (http, next) =>
        {
            using (CallerContextStamper.StampScope(CapabilityCallers.Of("stranger-user", "stranger-account", "b-stranger", CallerKind.User)))
            {
                await next(http);
            }
        });

        var appsGroup = BrainRoutes.Group(app, "/apps");
        appsGroup.MapGet("/{name}", (string name) => Results.Ok(new { App = name }));

        HandleRoutes.Map(app);

        await app.StartAsync(ct);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

            var nobodyResponse = await client.GetAsync("/nobody/apps/photocraft", ct);
            var vladResponse = await client.GetAsync("/vlad/apps/photocraft", ct);

            Assert.Equal(HttpStatusCode.Forbidden, nobodyResponse.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, vladResponse.StatusCode);
            Assert.Equal(nobodyResponse.StatusCode, vladResponse.StatusCode);

            var nobodyBody = await nobodyResponse.Content.ReadAsStringAsync(ct);
            var vladBody = await vladResponse.Content.ReadAsStringAsync(ct);
            Assert.Equal(nobodyBody, vladBody);

            // When access is permitted, /vlad/apps/photocraft rewrites and returns 200 OK
            access.AllowAccess = true;
            var allowedResponse = await client.GetAsync("/vlad/apps/photocraft", ct);
            Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);
        }
        finally
        {
            await app.StopAsync(ct);
            await app.DisposeAsync();
        }
    }

    private sealed class TestBrainAccess : IBrainAccess
    {
        public bool AllowAccess { get; set; }

        public ValueTask<bool> CanAccessAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(AllowAccess);
    }

    private sealed class FakeHandleDirectory(string? brainId) : IHandleDirectory
    {
        public Task<bool> Claim(string brainId) => Task.FromResult(true);
        public Task Release(string brainId) => Task.CompletedTask;
        public Task<string?> Resolve() => Task.FromResult(brainId);
    }
}
