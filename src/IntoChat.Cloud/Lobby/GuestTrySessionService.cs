using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Brains;
using DigitalBrain.Runtime.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.Cloud.Lobby;

public sealed record GuestTrySeedApp(
    string AppKey,
    string Title,
    string Summary,
    string IconKey,
    string Kind);

public sealed record GuestTrySession(
    string SessionId,
    string BrainId,
    string IpPrefix,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<GuestTrySeedApp> SeedApps)
{
    public int RemainingSeconds(DateTimeOffset now)
        => Math.Max(0, (int)Math.Ceiling((ExpiresAt - now).TotalSeconds));
}

public enum GuestTryProvisionOutcome
{
    Provisioned = 0,
    Resumed = 1,
    RateLimited = 2,
    CapacityReached = 3,
    UnauthorizedToken = 4,
}

public sealed record GuestTryProvisionResult(
    GuestTryProvisionOutcome Outcome,
    GuestTrySession? Session,
    string? SignedToken,
    int? RetryAfterSeconds = null);

public sealed class GuestTryOptions
{
    public TimeSpan SessionTtl { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan RateLimitWindow { get; set; } = TimeSpan.FromMinutes(30);
    public int MaxSessionsPerIpPrefixPerWindow { get; set; } = 5;
    public int MaxActiveSessions { get; set; } = 500;
}

public static class GuestTryToken
{
    public const string CookieName = "intochat_try";
    public const string HeaderName = "X-Try-Token";
    public const string BrainIdPrefix = "try-";

    public static string Issue(byte[] key, GuestTrySession session)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(session);

        var payload = BuildPayload(session.SessionId, session.BrainId, session.IpPrefix, session.ExpiresAt);
        return Base64UrlEncode(Encoding.UTF8.GetBytes(payload)) + "." + ComputeMacHex(key, payload);
    }

    public static bool TryValidate(
        byte[] key,
        string? token,
        DateTimeOffset now,
        out string sessionId,
        out string brainId,
        out string ipPrefix,
        out DateTimeOffset expiresAt)
    {
        sessionId = "";
        brainId = "";
        ipPrefix = "";
        expiresAt = default;

        if (key is null || key.Length < 16 || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var dot = token.LastIndexOf('.');
        if (dot <= 0 || dot == token.Length - 1)
        {
            return false;
        }

        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(Base64UrlDecode(token[..dot]));
        }
        catch (FormatException)
        {
            return false;
        }

        var providedMac = token[(dot + 1)..];
        var expectedMac = ComputeMacHex(key, payload);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(providedMac),
                Encoding.ASCII.GetBytes(expectedMac)))
        {
            return false;
        }

        var parts = payload.Split('|');
        if (parts.Length != 4 || !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expUnix))
        {
            return false;
        }

        var exp = DateTimeOffset.FromUnixTimeSeconds(expUnix);
        if (exp <= now)
        {
            return false;
        }

        var sid = parts[0];
        var bid = parts[1];
        var pfx = parts[2];

        if (string.IsNullOrEmpty(sid)
            || string.IsNullOrEmpty(pfx)
            || !string.Equals(bid, BrainIdPrefix + sid, StringComparison.Ordinal)
            || !BrainScope.IsValidId(bid))
        {
            return false;
        }

        sessionId = sid;
        brainId = bid;
        ipPrefix = pfx;
        expiresAt = exp;
        return true;
    }

    private static string BuildPayload(string sessionId, string brainId, string ipPrefix, DateTimeOffset expiresAt)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{sessionId}|{brainId}|{ipPrefix}|{expiresAt.ToUnixTimeSeconds()}");

    private static string ComputeMacHex(byte[] key, string payload)
        => Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload)));

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}

/// <summary>
/// Provisions rate-limited, strict-TTL ephemeral <c>try-&lt;id&gt;</c> guest brain sessions pre-seeded with
/// interactive demo micro-apps (<c>helloworld</c>, <c>btcalert</c>, <c>photocraft</c>).
/// </summary>
public sealed class GuestTrySessionService
{
    public static IReadOnlyList<GuestTrySeedApp> DefaultSeedDemoApps { get; } =
    [
        new(
            AppKey: "helloworld",
            Title: "Hello World",
            Summary: "Interactive Screen<HelloWorldState> micro-app where clicking Say Hello toggles live state.",
            IconKey: "brand:intochat",
            Kind: "unit"),
        new(
            AppKey: "btcalert",
            Title: "BTC < $85k Watcher",
            Summary: "Scheduled [Every(\"1m\")] market watcher firing instant desktop and inbox notifications.",
            IconKey: "brand:bitcoin",
            Kind: "unit"),
        new(
            AppKey: "photocraft",
            Title: "PhotoCraft",
            Summary: "Non-destructive creative image studio with layer compositing and instant background removal.",
            IconKey: "artcraft:photocraft",
            Kind: "studio"),
    ];

    private static readonly GuestTryOptions DefaultOptions = new();
    private readonly GuestTryOptions _options;
    private readonly Dictionary<string, GuestTrySession> _activeSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DateTimeOffset>> _ipIssuanceHistory = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public GuestTrySessionService(GuestTryOptions? options = null)
    {
        _options = options ?? DefaultOptions;
    }

    public int ActiveSessionCount
    {
        get
        {
            lock (_gate)
            {
                return _activeSessions.Count;
            }
        }
    }

    public GuestTryProvisionResult ProvisionOrResume(
        string? remoteIp,
        string? existingToken,
        byte[] signingKey,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        var callerPrefix = LobbyToken.PrefixOf(remoteIp);

        lock (_gate)
        {
            SweepExpiredLocked(now);

            if (!string.IsNullOrWhiteSpace(existingToken))
            {
                if (!GuestTryToken.TryValidate(signingKey, existingToken, now, out var sid, out _, out var tokenPrefix, out _)
                    || !string.Equals(tokenPrefix, callerPrefix, StringComparison.Ordinal))
                {
                    return new GuestTryProvisionResult(GuestTryProvisionOutcome.UnauthorizedToken, null, null);
                }

                if (_activeSessions.TryGetValue(sid, out var active) && active.ExpiresAt > now)
                {
                    return new GuestTryProvisionResult(GuestTryProvisionOutcome.Resumed, active, existingToken);
                }
            }

            if (!_ipIssuanceHistory.TryGetValue(callerPrefix, out var issuedTimestamps))
            {
                issuedTimestamps = [];
                _ipIssuanceHistory[callerPrefix] = issuedTimestamps;
            }

            if (issuedTimestamps.Count >= _options.MaxSessionsPerIpPrefixPerWindow)
            {
                var oldestInWindow = issuedTimestamps[0];
                var retryAfter = Math.Max(1, (int)Math.Ceiling((oldestInWindow + _options.RateLimitWindow - now).TotalSeconds));
                return new GuestTryProvisionResult(GuestTryProvisionOutcome.RateLimited, null, null, retryAfter);
            }

            if (_activeSessions.Count >= _options.MaxActiveSessions)
            {
                return new GuestTryProvisionResult(GuestTryProvisionOutcome.CapacityReached, null, null, RetryAfterSeconds: 60);
            }

            var sessionId = GenerateSessionId();
            var brainId = GuestTryToken.BrainIdPrefix + sessionId;
            if (!BrainScope.IsValidId(brainId))
            {
                throw new InvalidOperationException($"Generated guest try brain ID '{brainId}' violated BrainScope grammar.");
            }

            var expiresAt = now + _options.SessionTtl;
            var session = new GuestTrySession(
                SessionId: sessionId,
                BrainId: brainId,
                IpPrefix: callerPrefix,
                IssuedAt: now,
                ExpiresAt: expiresAt,
                SeedApps: DefaultSeedDemoApps);

            _activeSessions[sessionId] = session;
            issuedTimestamps.Add(now);

            var signedToken = GuestTryToken.Issue(signingKey, session);
            return new GuestTryProvisionResult(GuestTryProvisionOutcome.Provisioned, session, signedToken);
        }
    }

    public bool TryGetActive(
        string? remoteIp,
        string? token,
        byte[] signingKey,
        DateTimeOffset now,
        out GuestTrySession? session)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        session = null;

        var callerPrefix = LobbyToken.PrefixOf(remoteIp);

        lock (_gate)
        {
            SweepExpiredLocked(now);
            if (!GuestTryToken.TryValidate(signingKey, token, now, out var sessionId, out _, out var boundPrefix, out _)
                || !string.Equals(callerPrefix, boundPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            if (_activeSessions.TryGetValue(sessionId, out var found) && found.ExpiresAt > now)
            {
                session = found;
                return true;
            }

            return false;
        }
    }

    public bool IsActiveSession(string sessionId, string brainId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(brainId))
        {
            return false;
        }

        lock (_gate)
        {
            return _activeSessions.TryGetValue(sessionId, out var found)
                && found.ExpiresAt > now
                && string.Equals(found.BrainId, brainId, StringComparison.Ordinal);
        }
    }

    public bool Expire(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        lock (_gate)
        {
            return _activeSessions.Remove(sessionId);
        }
    }

    public int SweepExpired(DateTimeOffset now)
    {
        lock (_gate)
        {
            return SweepExpiredLocked(now);
        }
    }

    private int SweepExpiredLocked(DateTimeOffset now)
    {
        var removed = 0;
        foreach (var (id, session) in _activeSessions.ToArray())
        {
            if (session.ExpiresAt <= now)
            {
                _activeSessions.Remove(id);
                removed++;
            }
        }

        var window = _options.RateLimitWindow;
        foreach (var (prefix, timestamps) in _ipIssuanceHistory.ToArray())
        {
            timestamps.RemoveAll(ts => now - ts >= window);
            if (timestamps.Count == 0)
            {
                _ipIssuanceHistory.Remove(prefix);
            }
        }

        return removed;
    }

    private static string GenerateSessionId()
    {
        Span<byte> random = stackalloc byte[6];
        RandomNumberGenerator.Fill(random);
        return Convert.ToHexStringLower(random);
    }
}

internal sealed class GuestTryHttpSurface : IModuleHttpSurface
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/try/session", ProvisionAsync).AllowAnonymous();
        endpoints.MapGet("/try/session", InspectAsync).AllowAnonymous();
        endpoints.MapDelete("/try/session", EndAsync).AllowAnonymous();
    }

    internal static IResult Provision(
        HttpContext http,
        GuestTrySessionService service,
        ILobbyTokenKey keys,
        TimeProvider clock)
    {
        if (!LobbyHttpSurface.OriginAllowed(http.Request))
        {
            return Results.BadRequest(new { code = "ORIGIN" });
        }

        var now = clock.GetUtcNow();
        var remoteIp = http.Connection.RemoteIpAddress?.ToString();
        var existingToken = ExtractToken(http.Request);

        var outcome = service.ProvisionOrResume(remoteIp, existingToken, keys.Key, now);
        switch (outcome.Outcome)
        {
            case GuestTryProvisionOutcome.Provisioned:
            case GuestTryProvisionOutcome.Resumed:
                {
                    var session = outcome.Session!;
                    http.Response.Cookies.Append(GuestTryToken.CookieName, outcome.SignedToken!, BuildCookie(session.ExpiresAt));
                    return Results.Json(FormatSessionPayload(session, now, resumed: outcome.Outcome == GuestTryProvisionOutcome.Resumed));
                }

            case GuestTryProvisionOutcome.UnauthorizedToken:
                http.Response.Cookies.Delete(GuestTryToken.CookieName, BuildCookie(DateTimeOffset.UnixEpoch));
                return Results.Unauthorized();

            case GuestTryProvisionOutcome.RateLimited:
            case GuestTryProvisionOutcome.CapacityReached:
                {
                    var retrySeconds = outcome.RetryAfterSeconds ?? 60;
                    http.Response.Headers.RetryAfter = retrySeconds.ToString(CultureInfo.InvariantCulture);
                    return Results.Json(
                        new
                        {
                            error = outcome.Outcome == GuestTryProvisionOutcome.RateLimited ? "rate_limited" : "capacity_reached",
                            retryAfterSeconds = retrySeconds,
                        },
                        statusCode: StatusCodes.Status429TooManyRequests);
                }

            default:
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    internal static IResult Inspect(
        HttpContext http,
        GuestTrySessionService service,
        ILobbyTokenKey keys,
        TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var remoteIp = http.Connection.RemoteIpAddress?.ToString();
        var token = ExtractToken(http.Request);

        if (!service.TryGetActive(remoteIp, token, keys.Key, now, out var session) || session is null)
        {
            return Results.Unauthorized();
        }

        return Results.Json(FormatSessionPayload(session, now, resumed: true));
    }

    internal static IResult End(
        HttpContext http,
        GuestTrySessionService service,
        ILobbyTokenKey keys,
        TimeProvider clock)
    {
        if (!LobbyHttpSurface.OriginAllowed(http.Request))
        {
            return Results.BadRequest(new { code = "ORIGIN" });
        }

        var now = clock.GetUtcNow();
        var remoteIp = http.Connection.RemoteIpAddress?.ToString();
        var token = ExtractToken(http.Request);

        if (!service.TryGetActive(remoteIp, token, keys.Key, now, out var session) || session is null)
        {
            return Results.Unauthorized();
        }

        _ = service.Expire(session.SessionId);
        http.Response.Cookies.Delete(GuestTryToken.CookieName, BuildCookie(DateTimeOffset.UnixEpoch));
        return Results.Ok(new { ended = true, brainId = session.BrainId });
    }

    private static Task<IResult> ProvisionAsync(HttpContext http, GuestTrySessionService service, ILobbyTokenKey keys, TimeProvider clock)
        => Task.FromResult(Provision(http, service, keys, clock));

    private static Task<IResult> InspectAsync(HttpContext http, GuestTrySessionService service, ILobbyTokenKey keys, TimeProvider clock)
        => Task.FromResult(Inspect(http, service, keys, clock));

    private static Task<IResult> EndAsync(HttpContext http, GuestTrySessionService service, ILobbyTokenKey keys, TimeProvider clock)
        => Task.FromResult(End(http, service, keys, clock));

    public static string? ExtractToken(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Cookies.TryGetValue(GuestTryToken.CookieName, out var cookieVal) && !string.IsNullOrWhiteSpace(cookieVal))
        {
            return cookieVal;
        }

        if (request.Headers.TryGetValue(GuestTryToken.HeaderName, out var headerVal) && headerVal.Count == 1)
        {
            return headerVal[0]?.Trim();
        }

        return null;
    }

    private static object FormatSessionPayload(GuestTrySession session, DateTimeOffset now, bool resumed)
        => new
        {
            trySession = true,
            resumed,
            ephemeral = true,
            sessionId = session.SessionId,
            brainId = session.BrainId,
            ttlSeconds = session.RemainingSeconds(now),
            expiresAt = session.ExpiresAt,
            seedApps = session.SeedApps,
        };

    private static CookieOptions BuildCookie(DateTimeOffset expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Expires = expires,
    };
}
