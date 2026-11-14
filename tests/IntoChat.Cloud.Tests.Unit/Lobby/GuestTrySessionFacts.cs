using System.Net;
using System.Text.Json;
using DigitalBrain.Brains;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.Cloud.Lobby.Tests;

public sealed class GuestTrySessionFacts
{
    private static readonly DateTimeOffset StartTime = new(2026, 11, 14, 18, 0, 0, TimeSpan.Zero);
    private static readonly byte[] SigningKey = Enumerable.Repeat((byte)0x4B, 32).ToArray();

    [Fact]
    public void ProvisionIssuesIsolatedTryBrainScopeWithThirtyMinuteTtlAndSeedDemoApps()
    {
        var clock = new FakeTimeProvider(StartTime);
        var service = new GuestTrySessionService();
        var keys = new FixedTokenKey();

        var http = CreateHttpContext("203.0.113.10");
        var result = GuestTryHttpSurface.Provision(http, service, keys, clock);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        var setCookie = ReadSetCookie(http, GuestTryToken.CookieName);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(ReadBody(result));
        var root = doc.RootElement;
        Assert.True(root.GetProperty("trySession").GetBoolean());
        Assert.False(root.GetProperty("resumed").GetBoolean());
        Assert.True(root.GetProperty("ephemeral").GetBoolean());

        var brainId = root.GetProperty("brainId").GetString()!;
        Assert.StartsWith("try-", brainId, StringComparison.Ordinal);
        Assert.True(BrainScope.IsValidId(brainId));
        Assert.Equal(1800, root.GetProperty("ttlSeconds").GetInt32());

        var seedKeys = root.GetProperty("seedApps")
            .EnumerateArray()
            .Select(item => item.GetProperty("appKey").GetString()!)
            .ToArray();
        Assert.Equal(["helloworld", "btcalert", "photocraft"], seedKeys);
    }

    [Fact]
    public void ActiveSessionResumesIdempotentlyAndExpiresStrictlyAtThirtyMinutes()
    {
        var clock = new FakeTimeProvider(StartTime);
        var service = new GuestTrySessionService();
        var keys = new FixedTokenKey();

        var firstHttp = CreateHttpContext("198.51.100.25");
        var firstResult = GuestTryHttpSurface.Provision(firstHttp, service, keys, clock);
        var cookieToken = ExtractCookieValue(ReadSetCookie(firstHttp, GuestTryToken.CookieName));

        using var firstDoc = JsonDocument.Parse(ReadBody(firstResult));
        var firstBrainId = firstDoc.RootElement.GetProperty("brainId").GetString()!;

        // Advance 29 minutes: session resumes without allocating a second brain or burning rate-limit quota.
        clock.Advance(TimeSpan.FromMinutes(29));
        var resumeHttp = CreateHttpContext("198.51.100.42", cookieToken);
        var resumeResult = GuestTryHttpSurface.Provision(resumeHttp, service, keys, clock);
        Assert.Equal(StatusCodes.Status200OK, StatusOf(resumeResult));

        using var resumeDoc = JsonDocument.Parse(ReadBody(resumeResult));
        Assert.True(resumeDoc.RootElement.GetProperty("resumed").GetBoolean());
        Assert.Equal(firstBrainId, resumeDoc.RootElement.GetProperty("brainId").GetString());
        Assert.Equal(60, resumeDoc.RootElement.GetProperty("ttlSeconds").GetInt32());
        Assert.Equal(1, service.ActiveSessionCount);

        // Advance past 30-minute TTL: GET /try/session returns 401 Unauthorized and session is swept.
        clock.Advance(TimeSpan.FromMinutes(2));
        var expiredInspectHttp = CreateHttpContext("198.51.100.25", cookieToken);
        var expiredResult = GuestTryHttpSurface.Inspect(expiredInspectHttp, service, keys, clock);
        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(expiredResult));
        Assert.Equal(0, service.SweepExpired(clock.GetUtcNow()));
        Assert.Equal(0, service.ActiveSessionCount);
    }

    [Fact]
    public void PerIpPrefixRateLimiterBlocksBurstAndAllowsIndependentSubnets()
    {
        var clock = new FakeTimeProvider(StartTime);
        var options = new GuestTryOptions
        {
            SessionTtl = TimeSpan.FromMinutes(30),
            RateLimitWindow = TimeSpan.FromMinutes(30),
            MaxSessionsPerIpPrefixPerWindow = 5,
        };
        var service = new GuestTrySessionService(options);
        var keys = new FixedTokenKey();

        var distinctBrains = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i <= 5; i++)
        {
            var okHttp = CreateHttpContext($"203.0.113.{10 + i}");
            var okResult = GuestTryHttpSurface.Provision(okHttp, service, keys, clock);
            Assert.Equal(StatusCodes.Status200OK, StatusOf(okResult));
            using var parsed = JsonDocument.Parse(ReadBody(okResult));
            Assert.True(distinctBrains.Add(parsed.RootElement.GetProperty("brainId").GetString()!));
        }

        // 6th new guest on the same /24 prefix is throttled with 429 Too Many Requests + Retry-After.
        var throttledHttp = CreateHttpContext("203.0.113.99");
        var throttledResult = GuestTryHttpSurface.Provision(throttledHttp, service, keys, clock);
        Assert.Equal(StatusCodes.Status429TooManyRequests, StatusOf(throttledResult));
        Assert.False(string.IsNullOrWhiteSpace(throttledHttp.Response.Headers.RetryAfter.ToString()));

        // Independent IP prefix still provisions immediately.
        var otherSubnetHttp = CreateHttpContext("192.0.2.15");
        Assert.Equal(StatusCodes.Status200OK, StatusOf(GuestTryHttpSurface.Provision(otherSubnetHttp, service, keys, clock)));

        // Advancing past the 30-minute sliding window restores quota on the original prefix.
        clock.Advance(TimeSpan.FromMinutes(31));
        var recoveredHttp = CreateHttpContext("203.0.113.99");
        Assert.Equal(StatusCodes.Status200OK, StatusOf(GuestTryHttpSurface.Provision(recoveredHttp, service, keys, clock)));
    }

    [Fact]
    public void TamperedTokenForeignSubnetForeignOriginAndExplicitDeleteAreEnforced()
    {
        var clock = new FakeTimeProvider(StartTime);
        var service = new GuestTrySessionService();
        var keys = new FixedTokenKey();

        // Foreign Origin header is rejected with 400 Bad Request.
        var evilOriginHttp = CreateHttpContext("203.0.113.5");
        evilOriginHttp.Request.Headers.Origin = "https://attacker.example";
        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(GuestTryHttpSurface.Provision(evilOriginHttp, service, keys, clock)));

        // Issue a legitimate session.
        var goodHttp = CreateHttpContext("203.0.113.5");
        _ = GuestTryHttpSurface.Provision(goodHttp, service, keys, clock);
        var goodToken = ExtractCookieValue(ReadSetCookie(goodHttp, GuestTryToken.CookieName));

        // Tampered MAC is rejected with 401 Unauthorized.
        var tamperedToken = goodToken[..^1] + (goodToken[^1] == 'a' ? "b" : "a");
        Assert.Equal(
            StatusCodes.Status401Unauthorized,
            StatusOf(GuestTryHttpSurface.Provision(CreateHttpContext("203.0.113.5", tamperedToken), service, keys, clock)));

        // Foreign IP prefix presenting stolen cookie is rejected with 401 Unauthorized.
        Assert.Equal(
            StatusCodes.Status401Unauthorized,
            StatusOf(GuestTryHttpSurface.Inspect(CreateHttpContext("198.51.100.77", goodToken), service, keys, clock)));

        // Explicit DELETE /try/session revokes the session immediately.
        var endHttp = CreateHttpContext("203.0.113.5", goodToken);
        Assert.Equal(StatusCodes.Status200OK, StatusOf(GuestTryHttpSurface.End(endHttp, service, keys, clock)));
        Assert.Equal(0, service.ActiveSessionCount);
        Assert.Equal(
            StatusCodes.Status401Unauthorized,
            StatusOf(GuestTryHttpSurface.Inspect(CreateHttpContext("203.0.113.5", goodToken), service, keys, clock)));
    }

    private static DefaultHttpContext CreateHttpContext(string remoteIp, string? tryCookie = null)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        http.Request.Host = new HostString("os.intochat.io");
        if (!string.IsNullOrEmpty(tryCookie))
        {
            http.Request.Headers.Cookie = GuestTryToken.CookieName + "=" + tryCookie;
        }

        return http;
    }

    private static int StatusOf(IResult result)
        => result is IStatusCodeHttpResult coded ? coded.StatusCode ?? StatusCodes.Status200OK : StatusCodes.Status200OK;

    private static string ReadBody(IResult result)
    {
        var http = new DefaultHttpContext { RequestServices = new MinimalServices() };
        http.Response.Body = new MemoryStream();
        result.ExecuteAsync(http).GetAwaiter().GetResult();
        http.Response.Body.Position = 0;
        using var reader = new StreamReader(http.Response.Body);
        return reader.ReadToEnd();
    }

    private static string ReadSetCookie(HttpContext http, string cookieName)
    {
        foreach (var header in http.Response.Headers.SetCookie)
        {
            if (header is not null && header.StartsWith(cookieName + "=", StringComparison.Ordinal))
            {
                return header;
            }
        }

        return string.Empty;
    }

    private static string ExtractCookieValue(string setCookie)
    {
        var afterEq = setCookie[(setCookie.IndexOf('=') + 1)..];
        var semi = afterEq.IndexOf(';');
        return semi < 0 ? afterEq : afterEq[..semi];
    }

    private sealed class FixedTokenKey : ILobbyTokenKey
    {
        public byte[] Key => SigningKey;
    }

    private sealed class MinimalServices : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(Microsoft.Extensions.Logging.ILoggerFactory))
            {
                return Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
            }

            if (serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(Microsoft.Extensions.Logging.ILogger<>))
            {
                return Activator.CreateInstance(
                    typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>).MakeGenericType(serviceType.GetGenericArguments()));
            }

            return null;
        }
    }
}
