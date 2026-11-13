using System.Net;
using IntoChat.OS.Aspire.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Kernel.Tests.Integration;

public sealed class NginxProxyFacts : IAsyncLifetime
{
    private WebApplication? _app;
    private HttpClient? _client;
    private string _tempDir = "";

    public async ValueTask InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"intochat-web-shell-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "artcraft", "photocraft"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "artcraft", "filmcraft"));

        await File.WriteAllTextAsync(Path.Combine(_tempDir, "index.html"), "<html><body>shell-index</body></html>");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "artcraft", "photocraft", "index.html"), "<html><body>photocraft-index</body></html>");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "artcraft", "filmcraft", "index.html"), "<html><body>filmcraft-index</body></html>");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "config.json"), """{"kernelUrl":""}""");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "artcraft", "photocraft", "studio.js"), "studio-js");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            WebRootPath = _tempDir,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        _app = builder.Build();
        _app.UseStaticWebShell();

        // The handle route's shape: it claims every three-segment path, as the Identity module's does.
        _app.MapGet("/{handle}/{collection}/{key}", () => Results.StatusCode(StatusCodes.Status401Unauthorized));
        _app.MapGet("/alive", () => Results.Text("alive", "text/plain"));
        _app.MapGet("/ready", () => Results.Text("ready", "text/plain"));
        _app.MapGet("/auth/status", (HttpContext ctx) =>
        {
            ctx.Response.Headers.Append("Set-Cookie", "ic_session=abc; Path=/; HttpOnly; SameSite=Lax");
            return Results.Json(new { ok = true });
        });

        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();

        _client = new HttpClient
        {
            BaseAddress = new Uri(address),
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();

        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (!string.IsNullOrEmpty(_tempDir) && Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task StaticIndexServedDirectly()
    {
        var response = await _client!.GetAsync("/", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("shell-index", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArtcraftBundlesServedAndFallbackToIndex()
    {
        var photocraftRoot = await _client!.GetAsync("/artcraft/photocraft/", TestContext.Current.CancellationToken);
        var photocraftBody = await photocraftRoot.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, photocraftRoot.StatusCode);
        Assert.Contains("photocraft-index", photocraftBody, StringComparison.Ordinal);

        var photocraftDeep = await _client.GetAsync("/artcraft/photocraft/workspace/canvas", TestContext.Current.CancellationToken);
        var photocraftDeepBody = await photocraftDeep.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, photocraftDeep.StatusCode);
        Assert.Contains("photocraft-index", photocraftDeepBody, StringComparison.Ordinal);

        var filmcraftRoot = await _client.GetAsync("/artcraft/filmcraft/", TestContext.Current.CancellationToken);
        var filmcraftBody = await filmcraftRoot.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, filmcraftRoot.StatusCode);
        Assert.Contains("filmcraft-index", filmcraftBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileWinsOverARouteOfTheSameShape()
    {
        var asset = await _client!.GetAsync("/artcraft/photocraft/studio.js", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal("studio-js", await asset.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var index = await _client.GetAsync("/artcraft/photocraft/index.html", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);

        var route = await _client.GetAsync("/someone/apps/thing", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, route.StatusCode);
    }

    [Fact]
    public async Task ConfigJsonCarriesNoStoreHeaderWhenPresentAndWhenMissing()
    {
        var response = await _client!.GetAsync("/config.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);

        var configFile = Path.Combine(_tempDir, "config.json");
        File.Delete(configFile);
        try
        {
            var missingResponse = await _client.GetAsync("/config.json", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
            Assert.Contains("no-store", missingResponse.Headers.CacheControl?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await File.WriteAllTextAsync(configFile, """{"kernelUrl":""}""", TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task KernelEndpointsAndUnknownRoutesCoexistWithStaticShell()
    {
        var alive = await _client!.GetAsync("/alive", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
        Assert.Equal("alive", await alive.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var auth = await _client.GetAsync("/auth/status", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, auth.StatusCode);
        Assert.True(auth.Headers.TryGetValues("Set-Cookie", out var cookies));
        Assert.Contains(cookies, c => c.Contains("ic_session=abc", StringComparison.Ordinal));

        var notFound = await _client.GetAsync("/unknown-route", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
    }
}
