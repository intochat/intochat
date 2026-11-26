using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using IntoChat.OS.Host.Http;
using IntoChat.OS.Kernel.AspireServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Kernel.Tests.Integration;

public sealed partial class DownloadHubFacts : IAsyncLifetime
{
    private WebApplication? _app;
    private HttpClient? _client;
    private string _tempDir = "";

    public async ValueTask InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"intochat-download-hub-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "index.html"), "<html><body>shell-index</body></html>");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            WebRootPath = _tempDir,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        _app = builder.Build();
        _app.UseStaticWebShell();
        _app.MapDownloadHub();
        _app.MapGet("/{handle}/{collection}/{key}", () => Results.StatusCode(StatusCodes.Status401Unauthorized));

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
        try
        {
            _client?.Dispose();

            if (_app is not null)
            {
                try
                {
                    await _app.StopAsync();
                }
                finally
                {
                    await _app.DisposeAsync();
                }
            }
        }
        finally
        {
            DigitalBrain.Testing.TempDirectory.DeleteIfPresent(_tempDir);
        }
    }

    [Fact]
    public async Task GetDownloadReturnsSelfContainedHtmlWithAllPlatformLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await _client!.GetAsync(DownloadHubEndpoints.Path, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.True(Encoding.UTF8.GetByteCount(body) < 15 * 1024, "Expected /download response body under 15 KB.");

        Assert.Contains("IntoChat-Setup-win-x64.exe", body, StringComparison.Ordinal);
        Assert.Contains("intochat-win-x64.zip", body, StringComparison.Ordinal);
        Assert.Contains("install.ps1", body, StringComparison.Ordinal);
        Assert.Contains("IntoChat-macos-arm64.dmg", body, StringComparison.Ordinal);
        Assert.Contains("intochat-osx-arm64.tar.gz", body, StringComparison.Ordinal);
        Assert.Contains("install.sh", body, StringComparison.Ordinal);
        Assert.Contains("IntoChat-linux-x86_64.AppImage", body, StringComparison.Ordinal);
        Assert.Contains("intochat-linux-amd64.deb", body, StringComparison.Ordinal);
        Assert.Contains("intochat-linux-x64.tar.gz", body, StringComparison.Ordinal);
        Assert.Contains("intochat-android.apk", body, StringComparison.Ordinal);
        Assert.Contains("intochat-ios-unsigned.ipa", body, StringComparison.Ordinal);
        Assert.Contains("Add to Home Screen", body, StringComparison.Ordinal);
        Assert.Contains("href=\"/\"", body, StringComparison.Ordinal);
        Assert.DoesNotMatch(ExternalScriptOrStyleRegex(), body);
    }

    [Fact]
    public async Task HeadDownloadReturnsOkWithContentLengthAndEmptyBody()
    {
        var ct = TestContext.Current.CancellationToken;
        using var request = new HttpRequestMessage(HttpMethod.Head, DownloadHubEndpoints.Path);
        using var response = await _client!.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(DownloadHubEndpoints.PayloadByteLength, response.Content.Headers.ContentLength);
        Assert.Empty(body);
    }

    [Fact]
    public async Task GetAndHeadDownloadsPluralAliasReturnSelfContainedHtml()
    {
        var ct = TestContext.Current.CancellationToken;
        using var getResponse = await _client!.GetAsync(DownloadHubEndpoints.PluralPath, ct);
        var body = await getResponse.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal("text/html", getResponse.Content.Headers.ContentType?.MediaType);
        Assert.Contains("IntoChat-Setup-win-x64.exe", body, StringComparison.Ordinal);

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, DownloadHubEndpoints.PluralPath);
        using var headResponse = await _client!.SendAsync(headRequest, ct);
        Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
        Assert.Equal(DownloadHubEndpoints.PayloadByteLength, headResponse.Content.Headers.ContentLength);
    }

    [GeneratedRegex(@"<(script\b[^>]*\bsrc\s*=|link\b[^>]*\bhref\s*=\s*[""']?(https?:|//))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExternalScriptOrStyleRegex();
}
