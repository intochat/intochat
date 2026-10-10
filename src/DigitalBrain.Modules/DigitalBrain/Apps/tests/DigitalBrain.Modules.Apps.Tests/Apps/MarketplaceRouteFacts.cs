using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Framework;
using DigitalBrain.Modules.Apps.Tests;
using DigitalBrain.Modules.Apps.Tests.Runtimes;
using DigitalBrain.Testing.Module;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Apps.Tests.Apps;

// The Marketplace over HTTP: a publisher publishes, another brain browses, installs, sees an
// update when the publisher publishes again, and updates — all under the brain route.
public sealed class MarketplaceRouteFacts
{
    private const string Spec = "## Scenario: Echoes\nIt echoes.";

    [Fact(Timeout = 120_000)]
    public async Task PublishBrowseInstallAndUpdateAcrossTwoBrains()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().WithFakeAppRuntime(new()).StartAsync(ct);
        var publisherScope = BrainScope.Create("account-intochat", "intochat").Id;
        var source = brain.Get<IApp>(IApp.Key(publisherScope, "echo"));
        Caller.Clear();
        await AppOwner.Own(brain, "intochat");
        await source.Create(new("Echo", "echoes", [new("ask", "ask it")], [], [], Icon: "icon.svg"), Spec);
        await source.BeginBuild(null);
        await source.CompleteBuild(await source.CurrentBuildAttempt(),
            new() { ["synapses/echo.cs"] = "// v1", ["icon.svg"] = "<svg id=\"echo\"/>" }, null);

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IDigitalBrain>(brain.SiloServices.GetRequiredService<IDigitalBrain>());
        builder.Services.AddSingleton(brain.SiloServices.GetRequiredService<Creator>());
        builder.Services.AddSingleton(brain.SiloServices.GetRequiredService<AppWorkQueue>());
        builder.Services.AddSingleton(brain.SiloServices.GetRequiredService<MarketplaceView>());
        builder.Services.AddSingleton(brain.SiloServices.GetRequiredService<AppInstaller>());
        builder.Services.AddSingleton<IBrainAccess, OpenBrainAccess>();
        await using var edge = builder.Build();
        new AppsModule().Configure(edge);
        var endpoints = ((IEndpointRouteBuilder)edge).DataSources.SelectMany(item => item.Endpoints).OfType<RouteEndpoint>().ToArray();

        // Publish acts in the caller's own brain: alice has no echo to publish; intochat does.
        var refused = await Invoke("alice", "POST", "/brains/{brainId}/apps/{name}/publish", ("brainId", "alice"), ("name", "echo"));
        Assert.Equal(409, refused.Status);
        var released = (await source.Read()).Version;
        var published = await Invoke("intochat", "POST", "/brains/{brainId}/apps/{name}/publish", ("brainId", "intochat"), ("name", "echo"));
        Assert.True(published.Status == 200, published.Status + ": " + published.Body);
        Assert.Equal(released, Json(published).GetProperty("publishedVersion").GetString());

        // Another brain browses: the listing is there, not installed yet, and its icon serves.
        var listed = await Invoke("alice", "GET", "/brains/{brainId}/marketplace/", ("brainId", "alice"));
        Assert.Equal(200, listed.Status);
        var entry = Assert.Single(Json(listed).EnumerateArray().ToArray());
        Assert.Equal("intochat", entry.GetProperty("listing").GetProperty("publisher").GetString());
        Assert.False(entry.GetProperty("installed").GetBoolean());

        var icon = await Invoke("alice", "GET", "/brains/{brainId}/marketplace/{publisher}/{name}/icon", ("brainId", "alice"), ("publisher", "intochat"), ("name", "echo"));
        Assert.Equal(200, icon.Status);
        Assert.StartsWith("image/svg+xml", icon.ContentType, StringComparison.Ordinal);
        Assert.Equal("<svg id=\"echo\"/>", icon.Body);

        var missing = await Invoke("alice", "GET", "/brains/{brainId}/marketplace/{publisher}/{name}", ("brainId", "alice"), ("publisher", "intochat"), ("name", "nothing"));
        Assert.Equal(404, missing.Status);

        // Install lands in alice's apps with its origin; a second install clashes.
        var installed = await Invoke("alice", "POST", "/brains/{brainId}/marketplace/{publisher}/{name}/install", ("brainId", "alice"), ("publisher", "intochat"), ("name", "echo"));
        Assert.True(installed.Status == 200, installed.Status + ": " + installed.Body);
        Assert.Equal("intochat", Json(installed).GetProperty("origin").GetProperty("publisher").GetString());
        var installedIcon = await Invoke("alice", "GET", "/brains/{brainId}/apps/{name}/icon", ("brainId", "alice"), ("name", "echo"));
        Assert.Equal(200, installedIcon.Status);
        Assert.StartsWith("image/svg+xml", installedIcon.ContentType, StringComparison.Ordinal);
        Assert.Equal("<svg id=\"echo\"/>", installedIcon.Body);
        var noApp = await Invoke("alice", "GET", "/brains/{brainId}/apps/{name}/icon", ("brainId", "alice"), ("name", "nothing"));
        Assert.Equal(404, noApp.Status);
        var apps = await Invoke("alice", "GET", "/brains/{brainId}/apps/", ("brainId", "alice"));
        var item = Assert.Single(Json(apps).EnumerateArray().ToArray());
        Assert.Equal("icon.svg", item.GetProperty("icon").GetString());
        Assert.Equal("intochat", item.GetProperty("origin").GetProperty("publisher").GetString());
        Assert.Equal(released, item.GetProperty("origin").GetProperty("version").GetString());
        Assert.False(item.GetProperty("updateAvailable").GetBoolean());
        var again = await Invoke("alice", "POST", "/brains/{brainId}/marketplace/{publisher}/{name}/install", ("brainId", "alice"), ("publisher", "intochat"), ("name", "echo"));
        Assert.Equal(409, again.Status);

        // The publisher ships a new version; alice sees the update and takes it.
        Caller.Clear();
        await source.WriteBehavior("synapses/echo.cs", "// v2", Guid.NewGuid());
        var republished = await Invoke("intochat", "POST", "/brains/{brainId}/apps/{name}/publish", ("brainId", "intochat"), ("name", "echo"));
        var next = Json(republished).GetProperty("publishedVersion").GetString();
        var detail = await Invoke("alice", "GET", "/brains/{brainId}/marketplace/{publisher}/{name}", ("brainId", "alice"), ("publisher", "intochat"), ("name", "echo"));
        Assert.True(Json(detail).GetProperty("updateAvailable").GetBoolean());
        var updated = await Invoke("alice", "POST", "/brains/{brainId}/apps/{name}/update", ("brainId", "alice"), ("name", "echo"));
        Assert.True(updated.Status == 200, updated.Status + ": " + updated.Body);
        Assert.Equal(next, Json(updated).GetProperty("version").GetString());
        Assert.False(Json(updated).GetProperty("edited").GetBoolean());

        async Task<(int Status, string Body, string ContentType)> Invoke(string principal, string method, string pattern, params (string Key, string Value)[] values)
        {
            CallerContextStamper.Stamp(new CallerContext
            {
                PrincipalId = principal,
                AccountId = "account-" + principal,
                BrainId = principal,
                Kind = CallerKind.User,
                StampedBy = TrustedEdge.AuthenticatedHttp,
            });
            await brain.AuthorizeCallerAsync();
            var endpoint = endpoints.Single(item => item.RoutePattern.RawText == pattern
                && item.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
            var http = new DefaultHttpContext { RequestServices = edge.Services, RequestAborted = ct };
            http.Request.Method = method;
            http.Request.Headers.Authorization = "Basic test";
            foreach (var (key, value) in values) { http.Request.RouteValues[key] = value; }
            http.Response.Body = new MemoryStream();
            http.SetEndpoint(endpoint);
            await endpoint.RequestDelegate!(http);
            http.Response.Body.Position = 0;
            var body = await new StreamReader(http.Response.Body).ReadToEndAsync(ct);
            Caller.Clear();
            return (http.Response.StatusCode, body, http.Response.ContentType ?? "");
        }

        static JsonElement Json((int Status, string Body, string ContentType) result) => JsonDocument.Parse(result.Body).RootElement.Clone();
    }

    private sealed class OpenBrainAccess : IBrainAccess
    {
        public ValueTask<bool> CanAccessAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);
    }
}
