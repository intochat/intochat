using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Apps;

// An icon is any publisher's file served from the API origin: an SVG may carry script, so the
// answer must be inert — no script, no sniffing — whatever the file holds.
public sealed class AppIconFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task AnIconIsServedInertSoItsScriptNeverRuns()
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
        var files = new Dictionary<string, string> { ["icon.svg"] = "<svg><script>alert(document.cookie)</script></svg>" };

        await AppsEndpoints.Icon("icon.svg", files, "echo").ExecuteAsync(http);

        Assert.StartsWith("image/svg+xml", http.Response.ContentType, StringComparison.Ordinal);
        Assert.Equal("default-src 'none'; style-src 'unsafe-inline'; sandbox", http.Response.Headers.ContentSecurityPolicy.ToString());
        Assert.Equal("nosniff", http.Response.Headers.XContentTypeOptions.ToString());
    }
}
