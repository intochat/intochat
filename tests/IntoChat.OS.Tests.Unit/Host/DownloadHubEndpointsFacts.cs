using System.Text.RegularExpressions;
using IntoChat.OS.Host.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Tests.Unit.Host;

public sealed partial class DownloadHubEndpointsFacts
{
    private const int MaxPayloadBytes = 15 * 1024;

    [Fact]
    public void PayloadIsSelfContainedAndUnderFifteenKilobytes()
    {
        Assert.True(
            DownloadHubEndpoints.PayloadByteLength > 0 && DownloadHubEndpoints.PayloadByteLength < MaxPayloadBytes,
            $"Expected /download HTML payload to be under {MaxPayloadBytes} bytes, got {DownloadHubEndpoints.PayloadByteLength} bytes.");
    }

    [Fact]
    public void HtmlContainsAllFivePlatformsReleaseLinksCommandsAndBrowserCta()
    {
        var html = DownloadHubEndpoints.Html;
        var baseUrl = DownloadHubEndpoints.ReleaseDownloadBase;

        string[] expectedAssets =
        [
            $"{baseUrl}/IntoChat-Setup-win-x64.exe",
            $"{baseUrl}/intochat-win-x64.zip",
            $"irm {baseUrl}/install.ps1 | iex",
            $"{baseUrl}/IntoChat-macos-arm64.dmg",
            $"{baseUrl}/intochat-osx-arm64.tar.gz",
            $"curl -fsSL {baseUrl}/install.sh | sh",
            $"{baseUrl}/IntoChat-linux-x86_64.AppImage",
            $"{baseUrl}/intochat-linux-amd64.deb",
            $"{baseUrl}/intochat-linux-x64.tar.gz",
            $"{baseUrl}/intochat-android.apk",
            $"{baseUrl}/intochat-ios-unsigned.ipa",
        ];

        foreach (var expected in expectedAssets)
        {
            Assert.Contains(expected, html, StringComparison.Ordinal);
        }

        Assert.Contains("Add to Home Screen", html, StringComparison.Ordinal);
        Assert.Contains("Open Live IntoChat OS in Browser", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/\"", html, StringComparison.Ordinal);
        Assert.Contains("navigator.userAgent", html, StringComparison.Ordinal);
        Assert.Contains("prefers-color-scheme:dark", html, StringComparison.Ordinal);
        Assert.Contains("data-os=\"windows\"", html, StringComparison.Ordinal);
        Assert.Contains("data-os=\"macos\"", html, StringComparison.Ordinal);
        Assert.Contains("data-os=\"linux\"", html, StringComparison.Ordinal);
        Assert.Contains("data-os=\"android\"", html, StringComparison.Ordinal);
        Assert.Contains("data-os=\"ios\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlHasZeroExternalScriptStyleFontOrImageDependencies()
    {
        var html = DownloadHubEndpoints.Html;

        Assert.DoesNotMatch(ExternalScriptRegex(), html);
        Assert.DoesNotMatch(ExternalStylesheetOrLinkRegex(), html);
        Assert.DoesNotMatch(ExternalImageRegex(), html);
        Assert.DoesNotMatch(CssImportRegex(), html);
        Assert.DoesNotContain("fonts.googleapis.com", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unpkg.com", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("jsdelivr.net", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cdnjs.cloudflare.com", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapDownloadHubRegistersAnonymousGetEndpoint()
    {
        var app = WebApplication.CreateBuilder().Build();
        app.MapDownloadHub();

        foreach (var path in new[] { DownloadHubEndpoints.Path, DownloadHubEndpoints.PluralPath })
        {
            var endpoint = ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(static source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Single(route => string.Equals(route.RoutePattern.RawText, path, StringComparison.Ordinal));

            Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            Assert.NotNull(methods);
            Assert.Contains("GET", methods);
            Assert.Contains("HEAD", methods);
        }
    }

    [GeneratedRegex(@"<script\b[^>]*\bsrc\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExternalScriptRegex();

    [GeneratedRegex(@"<link\b[^>]*\bhref\s*=\s*[""']?(https?:|//)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExternalStylesheetOrLinkRegex();

    [GeneratedRegex(@"<img\b[^>]*\bsrc\s*=\s*[""']?(https?:|//)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExternalImageRegex();

    [GeneratedRegex(@"@import\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CssImportRegex();
}
