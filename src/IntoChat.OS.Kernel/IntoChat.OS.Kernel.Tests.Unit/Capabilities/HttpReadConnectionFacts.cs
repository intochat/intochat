using System.Net;
using DigitalBrain.Capabilities.Http;
using IntoChat.OS.Kernel.Composition.Capabilities;
using Xunit;

namespace IntoChat.OS.Kernel.Tests.Unit.Capabilities;

// The http@1 connection's reads: the allow-list holds on the url and on every redirect hop, and the body is capped.
public sealed class HttpReadConnectionFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Answers each url from a script; records what was asked.
    private sealed class Scripted(Func<Uri, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!);
            return Task.FromResult(answer(request.RequestUri!));
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage Redirect(string to) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(to) } };

    [Fact]
    public async Task HostsMatchExactlyOrBySuffix()
    {
        var handler = new Scripted(_ => Ok("{\"n\":1}"));
        var http = new HostBoundHttp(new HttpClient(handler));

        Assert.Equal("{\"n\":1}", await http.GetText(new Uri("https://api.x.com/a"), ["api.x.com"], Ct));
        Assert.Equal(1, (await http.GetJson(new Uri("https://deep.cdn.x.com/b"), ["*.x.com"], Ct)).GetProperty("n").GetInt32());
        await Assert.ThrowsAsync<HostNotAllowedException>(() => http.GetText(new Uri("https://x.com/c"), ["*.x.com"], Ct));
        await Assert.ThrowsAsync<HostNotAllowedException>(() => http.GetText(new Uri("https://api.y.com/d"), ["api.x.com"], Ct));
        Assert.Equal(2, handler.Asked.Count);
    }

    [Fact]
    public async Task ARedirectToAHostNotAllowedIsRefused()
    {
        var handler = new Scripted(url => url.Host == "api.x.com" ? Redirect("https://evil.example/steal") : Ok("never"));
        var http = new HostBoundHttp(new HttpClient(handler));

        await Assert.ThrowsAsync<HostNotAllowedException>(() => http.GetText(new Uri("https://api.x.com/a"), ["api.x.com"], Ct));

        Assert.DoesNotContain(handler.Asked, url => url.Host == "evil.example");
    }

    [Fact]
    public async Task RedirectsWithinTheListAreFollowedAtMostFiveTimes()
    {
        var follows = new HostBoundHttp(new HttpClient(new Scripted(url => url.AbsolutePath == "/a" ? Redirect("https://cdn.x.com/b") : Ok("done"))));
        Assert.Equal("done", await follows.GetText(new Uri("https://api.x.com/a"), ["api.x.com", "*.x.com"], Ct));

        var loops = new Scripted(url => Redirect(url.ToString()));
        await Assert.ThrowsAsync<HttpRequestException>(() => new HostBoundHttp(new HttpClient(loops)).GetText(new Uri("https://api.x.com/a"), ["api.x.com"], Ct));
        Assert.Equal(HostBoundHttp.MaxRedirects + 1, loops.Asked.Count);
    }

    [Fact]
    public async Task ABodyOverOneMebibyteIsRefused()
    {
        var http = new HostBoundHttp(new HttpClient(new Scripted(_ => Ok(new string('a', HostBoundHttp.MaxBytes + 1)))));

        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetText(new Uri("https://api.x.com/a"), ["api.x.com"], Ct));
    }

    [Fact]
    public async Task ABodyThatIsNotJsonIsAnHttpError()
    {
        var http = new HostBoundHttp(new HttpClient(new Scripted(_ => Ok("<html>nope</html>"))));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetJson(new Uri("https://api.x.com/a"), ["api.x.com"], Ct));

        Assert.Equal("api.x.com did not return JSON", error.Message);
    }

    [Fact]
    public void TheAllowListParsesCommasOrAJsonArray()
    {
        Assert.Equal(["api.x.com", "*.y.org"], IConnectionHosts.Parse(" api.x.com, *.y.org ,"));
        Assert.Equal(["api.x.com"], IConnectionHosts.Parse("[\"api.x.com\", \" \"]"));
        Assert.True(IConnectionHosts.Covers(["*.x.com"], "api.x.com"));
        Assert.False(IConnectionHosts.Covers(["*.x.com"], "x.com"));
    }
}
