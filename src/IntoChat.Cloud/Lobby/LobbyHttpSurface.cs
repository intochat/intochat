using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.Cloud.Lobby;

internal sealed class LobbyHttpSurface : IModuleHttpSurface
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/lobby/session", SessionAsync).AllowAnonymous();
        endpoints.MapPost("/lobby/handoff", HandoffAsync).AllowAnonymous();
        endpoints.MapPost("/lobby/google/callback", GoogleCallbackAsync).AllowAnonymous();
    }

    internal static IResult Session(HttpContext http, LobbyModuleOptions options, LobbyVisitorBudget budget, ILobbyTokenKey keys, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var prefix = LobbyToken.PrefixOf(http.Connection.RemoteIpAddress?.ToString());
        var existing = http.Request.Cookies[LobbyToken.CookieName];
        if (!string.IsNullOrEmpty(existing))
        {
            if (!LobbyToken.TryRead(keys.Key, existing, now, out var visitor, out var bound) || bound != prefix)
            {
                return Results.Unauthorized();
            }

            return Results.Json(new { lobby = true, brainId = options.BrainId, chromeless = options.Chromeless, visitorId = visitor });
        }

        var created = Guid.NewGuid().ToString("N");
        if (!budget.TryAdmit(prefix, created, now, options.VisitorsPerPrefix, LobbyToken.Lifetime))
        {
            return Results.Json(new { lobby = true, brainId = options.BrainId, chromeless = options.Chromeless, unavailable = true });
        }

        var token = LobbyToken.Issue(keys.Key, created, prefix, now + LobbyToken.Lifetime);
        http.Response.Cookies.Append(LobbyToken.CookieName, token, Cookie(now + LobbyToken.Lifetime));
        return Results.Json(new { lobby = true, brainId = options.BrainId, chromeless = options.Chromeless, visitorId = created });
    }

    internal static async Task<IResult> Handoff(HttpContext http, INeuronProxies proxies, LobbyModuleOptions options, ILobbyTokenKey keys, ISessionIssuer issuer, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!OriginAllowed(http.Request)) { return Results.BadRequest(new { code = "ORIGIN" }); }
        var now = clock.GetUtcNow();
        var prefix = LobbyToken.PrefixOf(http.Connection.RemoteIpAddress?.ToString());
        if (!LobbyToken.TryRead(keys.Key, http.Request.Cookies[LobbyToken.CookieName], now, out var visitor, out var bound) || bound != prefix)
        {
            return Results.Unauthorized();
        }

        var code = await ReadCode(http.Request);
        var neuron = proxies.Get<LobbyNeuron>(new NeuronId(new BrainId(options.BrainId), LobbyNeuron.Type, visitor));
        var member = code is null ? null : await neuron.Redeem(code, visitor, now);
        if (member is null) { return Results.BadRequest(new { code = "HANDOFF_INVALID" }); }
        await issuer.IssueAsync(http, new SessionRequest { Member = member }, cancellationToken);
        http.Response.Cookies.Delete(LobbyToken.CookieName, Cookie(DateTimeOffset.UnixEpoch));
        return Results.Ok(new { handedOff = true });
    }

    private static Task<IResult> SessionAsync(HttpContext http, LobbyModuleOptions options, LobbyVisitorBudget budget, ILobbyTokenKey keys, TimeProvider clock)
        => Task.FromResult(Session(http, options, budget, keys, clock));

    private static Task<IResult> HandoffAsync(HttpContext http, INeuronProxies proxies, LobbyModuleOptions options, ILobbyTokenKey keys, ISessionIssuer issuer, TimeProvider clock, CancellationToken cancellationToken)
        => Handoff(http, proxies, options, keys, issuer, clock, cancellationToken);

    internal static async Task<IResult> GoogleCallbackAsync(HttpContext http, INeuronProxies proxies, LobbyModuleOptions options, ILobbyTokenKey keys, TimeProvider clock)
    {
        if (!OriginAllowed(http.Request)) { return Results.BadRequest(new { code = "ORIGIN" }); }
        var now = clock.GetUtcNow();
        var prefix = LobbyToken.PrefixOf(http.Connection.RemoteIpAddress?.ToString());
        if (!LobbyToken.TryRead(keys.Key, http.Request.Cookies[LobbyToken.CookieName], now, out var visitor, out var bound) || bound != prefix)
        {
            return Results.BadRequest(new { code = "LOBBY_REQUIRED" });
        }

        var state = http.Request.Query["state"].ToString();
        var neuron = proxies.Get<LobbyNeuron>(new NeuronId(new BrainId(options.BrainId), LobbyNeuron.Type, visitor));
        var nonce = neuron.GoogleNonce;
        if (nonce is null || !LobbyToken.StateMatches(keys.Key, state, visitor, nonce, now))
        {
            await neuron.RejectGoogle();
            return Results.BadRequest(new { code = "STATE_MISMATCH" });
        }

        return Results.Ok(new { visitorId = visitor });
    }

    internal static bool OriginAllowed(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Origin", out var origin) || string.IsNullOrEmpty(origin)) { return true; }
        if (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri)) { return false; }
        return string.Equals(uri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> ReadCode(HttpRequest request)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body);
            return document.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CookieOptions Cookie(DateTimeOffset expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Expires = expires,
    };
}
