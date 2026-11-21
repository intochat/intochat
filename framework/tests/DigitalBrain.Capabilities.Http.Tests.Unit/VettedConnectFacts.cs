using System.Net;
using System.Text;
using DigitalBrain.Capabilities.Http;

namespace DigitalBrain.Capabilities.Http.Tests.Unit;

// The connect behind every vetted HTTP read: only a public address of the host is connected to,
// an IP in an allow-listed read must be named explicitly, and GetPublic vets every redirect hop.
public sealed class VettedConnectFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.1")]
    public void InwardAddressesAreNotPublic(string address) => Assert.False(VettedConnect.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("198.20.0.1")]
    [InlineData("2606:4700::1111")]
    public void PublicAddressesAre(string address) => Assert.True(VettedConnect.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public void AHostResolvingOnlyInwardIsRefusedAndAMixedOneKeepsItsPublicAddresses()
    {
        Assert.Throws<HostNotAllowedException>(() => VettedConnect.Public("api.x.com", [IPAddress.Loopback, IPAddress.Parse("10.0.0.5")]));
        Assert.Equal([IPAddress.Parse("93.184.216.34")], VettedConnect.Public("api.x.com", [IPAddress.Parse("127.0.0.1"), IPAddress.Parse("93.184.216.34")]));
    }

    // An allowed name the fake resolver points at loopback never opens a socket: the read fails before the request is sent.
    [Fact]
    public async Task AnAllowedNameResolvingToLoopbackIsRefusedThroughTheRealHandler()
    {
        var asked = new List<string>();
        Task<IPAddress[]> Resolve(string host, CancellationToken ct) { asked.Add(host); return Task.FromResult(new[] { IPAddress.Loopback }); }
        using var client = new HttpClient(HostBoundHttp.Handler(Resolve));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => new HostBoundHttp(client).GetText(new Uri("http://api.x.com/a"), ["api.x.com"], Ct));

        Assert.Equal(["api.x.com"], asked);
        Assert.True(error is HostNotAllowedException || error.InnerException is HostNotAllowedException, error.ToString());
    }

    [Fact]
    public async Task ALiteralIpPassesOnlyWhenTheListNamesIt()
    {
        var handler = new CountingHandler();
        var http = new HostBoundHttp(new HttpClient(handler));

        await Assert.ThrowsAsync<HostNotAllowedException>(() => http.GetText(new Uri("http://93.184.216.34/a"), ["*"], Ct));
        await Assert.ThrowsAsync<HostNotAllowedException>(() => http.GetText(new Uri("http://[2606:4700::1111]/a"), ["api.x.com"], Ct));
        Assert.Equal("ok", await http.GetText(new Uri("http://93.184.216.34/a"), ["93.184.216.34"], Ct));
        Assert.Equal(1, handler.Count);
    }

    // A redirect that loops names the host the unit asked for, not the one it was sent to.
    [Fact]
    public async Task ErrorsNameOnlyTheAskedHost()
    {
        var hops = 0;
        var handler = new Answering(url => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri($"https://cdn{++hops}.x.com/") } });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new HostBoundHttp(new HttpClient(handler)).GetText(new Uri("https://api.x.com/a"), ["*.x.com"], Ct));

        Assert.Contains("api.x.com", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPublicFollowsAPublicRedirect()
    {
        var asked = new List<Uri>();
        var handler = new Answering(url =>
        {
            asked.Add(url);
            return url.Host == "start.example.org"
                ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://final.example.net/page") } }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("hello", Encoding.UTF8, "text/plain") };
        });
        var http = new HostBoundHttp(new HttpClient(handler));

        var fetched = await http.GetPublic(new Uri("https://start.example.org/go"), Ct);

        Assert.Equal(new Uri("https://final.example.net/page"), fetched.FinalUrl);
        Assert.Equal("text/plain", fetched.MediaType);
        Assert.Equal("hello", fetched.Text);
        Assert.Equal(2, asked.Count);
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://localhost:8080/")]
    public async Task RedirectToPrivateIsRefused(string privateTarget)
    {
        var asked = new List<Uri>();
        var handler = new Answering(url =>
        {
            asked.Add(url);
            return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(privateTarget) } };
        });
        var http = new HostBoundHttp(new HttpClient(handler));

        await Assert.ThrowsAsync<HostNotAllowedException>(() => http.GetPublic(new Uri("https://public.example.org/start"), Ct));

        Assert.Single(asked);
    }

    [Fact]
    public async Task GetPublicReportsMediaTypeAndFinalUrl()
    {
        var handler = new Answering(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<h1>Hi</h1>", Encoding.UTF8, "text/html"),
        });
        var http = new HostBoundHttp(new HttpClient(handler));

        var fetched = await http.GetPublic(new Uri("https://docs.example.org/guide"), Ct);

        Assert.Equal(new Uri("https://docs.example.org/guide"), fetched.FinalUrl);
        Assert.Equal("text/html", fetched.MediaType);
        Assert.Equal("<h1>Hi</h1>", fetched.Text);
    }

    [Fact]
    public async Task BodyOver1MiBIsRefused()
    {
        var handler = new Answering(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', HostBoundHttp.MaxBytes + 1), Encoding.UTF8, "text/html"),
        });
        var http = new HostBoundHttp(new HttpClient(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetPublic(new Uri("https://big.example.org/page"), Ct));
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        }
    }

    private sealed class Answering(Func<Uri, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(answer(request.RequestUri!));
    }
}
