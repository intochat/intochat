using System.Net;
using DigitalBrain.Capabilities.Http;
using IntoChat.OS.Kernel.Composition.Capabilities;
using Xunit;

namespace IntoChat.OS.Kernel.Tests.Unit.Capabilities;

// The connect behind every http@1 read: only a public address of the host is connected to, and an IP in the url must be on the list.
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
    public void InwardAddressesAreNotPublic(string address) => Assert.False(VettedConnect.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("172.32.0.1")]
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
