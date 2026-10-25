using System.Net;
using System.Text;
using IntoChat.OS.Framework.Modules.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Framework.Tests.Unit;

public sealed class SdkHttpFacts
{
    [Theory]
    [InlineData("/", true)]
    [InlineData("/brains/a?x=1", true)]
    [InlineData("//evil.example", false)]
    [InlineData("/\\evil.example", false)]
    [InlineData("https://app.example.com/after", true)]
    [InlineData("https://APP.example.com", true)]
    [InlineData("http://app.example.com/after", false)]
    [InlineData("https://user@app.example.com/", false)]
    [InlineData("https://other.example.com/", false)]
    [InlineData("/line\nbreak", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ReturnUrlsAreLocalOrOnAnAllowedOrigin(string? url, bool allowed)
        => Assert.Equal(allowed, new ReturnUrlValidator(["https://app.example.com/"]).IsAllowed(url));

    [Theory]
    [InlineData("203.0.113.77", "203.0.113.0/24")]
    [InlineData("::ffff:203.0.113.77", "203.0.113.0/24")]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
    [InlineData("not-an-ip", IpPrefix.Unknown)]
    [InlineData(null, IpPrefix.Unknown)]
    public void AddressesAreTruncatedToTheirNetwork(string? address, string expected) => Assert.Equal(expected, IpPrefix.Truncate(address));

    [Fact]
    public async Task SlidingWindowRefusesOverTheLimitAndRecovers()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var throttle = new SlidingWindowThrottle(2, TimeSpan.FromMinutes(1), clock);
        var ct = TestContext.Current.CancellationToken;

        Assert.True((await throttle.HitAsync("a", ct)).Allowed);
        Assert.True((await throttle.HitAsync("a", ct)).Allowed);
        var refused = await throttle.HitAsync("a", ct);
        Assert.False(refused.Allowed);
        Assert.Equal(clock.GetUtcNow().AddMinutes(1), refused.RetryAt);
        Assert.True((await throttle.HitAsync("b", ct)).Allowed);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await throttle.HitAsync("a", ct)).Allowed);
    }

    [Fact]
    public async Task ThrottleFilterAnswers429OncePrefixIsSpent()
    {
        var filter = new IpThrottleFilter(new SlidingWindowThrottle(1, TimeSpan.FromMinutes(1), TimeProvider.System));
        var calls = 0;
        ValueTask<object?> next(EndpointFilterInvocationContext _) { calls++; return ValueTask.FromResult<object?>(Results.Ok()); }

        await filter.InvokeAsync(Context("198.51.100.1"), next);
        var second = await filter.InvokeAsync(Context("198.51.100.2"), next);
        await filter.InvokeAsync(Context("192.0.2.1"), next);

        Assert.Equal(2, calls);
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(second);
        Assert.Equal(StatusCodes.Status429TooManyRequests, status.StatusCode);
    }

    [Fact]
    public async Task ProblemsBodyReaderIsBounded()
    {
        var tooLarge = Request("{\"a\":\"" + new string('x', HttpProblems.MaxBodyBytes) + "\"}");
        var (_, failure) = await HttpProblems.ReadAsync<Dictionary<string, string>>(tooLarge.Request);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, Assert.IsAssignableFrom<IStatusCodeHttpResult>(failure).StatusCode);

        var (body, ok) = await HttpProblems.ReadAsync<Dictionary<string, string>>(Request("{\"a\":\"b\"}").Request);
        Assert.Null(ok);
        Assert.Equal("b", body!["a"]);

        var (_, invalid) = await HttpProblems.ReadAsync<Dictionary<string, string>>(Request("{").Request);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(invalid).StatusCode);
    }

    [Fact]
    public void ForwardingIsIgnoredUntilAProxyIsNamed()
    {
        var none = ForwardedHeadersSetup.CreateOptions(Configuration());
        Assert.Equal(ForwardedHeaders.None, none.ForwardedHeaders);
        Assert.Empty(none.KnownProxies);

        var pinned = ForwardedHeadersSetup.CreateOptions(Configuration(("KnownProxies:0", "10.0.0.1"), ("AllowedHosts:0", "os.example.com")));
        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost, pinned.ForwardedHeaders);
        Assert.Contains(IPAddress.Parse("10.0.0.1"), pinned.KnownProxies);

        Assert.Throws<InvalidOperationException>(() => ForwardedHeadersSetup.CreateOptions(Configuration(("AllowedHosts:0", "*.example.com"))));
        Assert.Throws<InvalidOperationException>(() => ForwardedHeadersSetup.CreateOptions(Configuration(("ForwardLimit", "9"))));
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(pair =>
            new KeyValuePair<string, string?>(ForwardedHeadersSetup.DefaultSection + ":" + pair.Key, pair.Value))).Build();

    private static DefaultEndpointFilterInvocationContext Context(string address)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(address);
        return new DefaultEndpointFilterInvocationContext(http);
    }

    private static DefaultHttpContext Request(string json)
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return http;
    }
}
