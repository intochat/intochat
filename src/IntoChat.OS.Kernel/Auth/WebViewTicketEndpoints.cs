using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using IntoChat.OS.Modules.Identity.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Kernel.Auth;

/// <summary>
/// One-Time Launch Ticket SSO endpoints for internal IntoChat OS WebViews
/// (Orleans Dashboard, DevUI, Widgetbook, etc.).
///
/// An authenticated shell client calls <c>POST /auth/webview-ticket</c> with a safe
/// relative <c>target</c> path and receives a short-lived, single-use
/// <c>/auth/webview-enter?ticket=...</c> URL. The embedded WebView navigates to that URL,
/// which exchanges the ticket for the host's standard session cookie and redirects to
/// <c>target</c>.
/// </summary>
public static class WebViewTicketEndpoints
{
    public const string MintPath = "/auth/webview-ticket";
    public const string EnterPath = "/auth/webview-enter";

    private static readonly TimeSpan SessionCookieLifetime = TimeSpan.FromHours(12);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(MintPath, async (HttpContext http, IOptions<AuthOptions> authOptions, WebViewTickets tickets) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            string? target;
            try
            {
                using var doc = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("target", out var targetProp)
                    || targetProp.ValueKind != JsonValueKind.String)
                {
                    return Results.BadRequest(new { error = "Request body must be a JSON object with a string 'target' property." });
                }

                target = targetProp.GetString();
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Invalid JSON body." });
            }

            if (!IsSafeRelativeTarget(target))
            {
                return Results.BadRequest(new { error = "Invalid target path. Must be a safe relative path starting with '/'." });
            }

            var principal = ResolveCookiePrincipal(http, authOptions.Value);
            if (principal is null)
            {
                return Results.Unauthorized();
            }

            var enterUrl = tickets.Issue(principal, target!);
            return Results.Ok(new { enterUrl });
        });

        endpoints.MapGet(EnterPath, async (HttpContext http, WebViewTickets tickets) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            var token = http.Request.Query["ticket"].ToString();
            if (string.IsNullOrWhiteSpace(token))
            {
                return Results.Unauthorized();
            }

            var now = DateTimeOffset.UtcNow;
            if (!tickets.TryRedeem(token, now, out var entry))
            {
                return Results.Unauthorized();
            }

            await http.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                entry.Principal,
                new AuthenticationProperties
                {
                    ExpiresUtc = now + SessionCookieLifetime,
                    IsPersistent = false,
                });

            return Results.LocalRedirect(entry.Target);
        }).AllowAnonymous();
    }

    /// <summary>
    /// Validates that <paramref name="target"/> is a safe local relative path
    /// (starts with <c>/</c>, not <c>//</c> or <c>/\</c>, no control characters).
    /// </summary>
    public static bool IsSafeRelativeTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        if (target[0] != '/')
        {
            return false;
        }

        if (target.Length > 1 && (target[1] == '/' || target[1] == '\\'))
        {
            return false;
        }

        for (var i = 0; i < target.Length; i++)
        {
            var c = target[i];
            if (c < 0x20 || c == 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    private static ClaimsPrincipal? ResolveCookiePrincipal(HttpContext http, AuthOptions options)
    {
        if (http.User.Identity?.IsAuthenticated == true)
        {
            return new ClaimsPrincipal(
                new ClaimsIdentity(http.User.Claims, CookieAuthenticationDefaults.AuthenticationScheme));
        }

        var caller = AccountSession.ResolveCaller(http, options);
        if (caller?.For is null)
        {
            return null;
        }

        var sessionUser = AccountSession.SessionUser(caller);
        return new ClaimsPrincipal(
            new ClaimsIdentity(sessionUser.Claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}

// Live launch tickets for one host: content-keyed and short-lived, so they are DI-owned rather than a process static.
public sealed class WebViewTickets
{
    private static readonly TimeSpan TicketTtl = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, WebViewTicket> _tickets = new(StringComparer.Ordinal);

    internal sealed record WebViewTicket(ClaimsPrincipal Principal, string Target, DateTimeOffset ExpiresAt);

    /// <summary>
    /// Mints a short-lived, single-use <c>/auth/webview-enter?ticket=...</c> URL for
    /// <paramref name="principal"/> targeting <paramref name="relativePath"/>.
    /// </summary>
    public string Issue(ClaimsPrincipal principal, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!WebViewTicketEndpoints.IsSafeRelativeTarget(relativePath))
        {
            throw new ArgumentException("Invalid target path. Must be a safe relative path starting with '/'.", nameof(relativePath));
        }

        var cookiePrincipal = string.Equals(principal.Identity?.AuthenticationType, CookieAuthenticationDefaults.AuthenticationScheme, StringComparison.Ordinal)
            ? principal
            : new ClaimsPrincipal(new ClaimsIdentity(principal.Claims, CookieAuthenticationDefaults.AuthenticationScheme));

        var now = DateTimeOffset.UtcNow;
        PruneExpired(now);

        var token = CreateBase64UrlToken();
        _tickets[token] = new WebViewTicket(cookiePrincipal, relativePath, now + TicketTtl);

        return $"{WebViewTicketEndpoints.EnterPath}?ticket={Uri.EscapeDataString(token)}";
    }

    internal bool TryRedeem(string token, DateTimeOffset now, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WebViewTicket? entry)
    {
        if (!_tickets.TryRemove(token, out entry))
        {
            return false;
        }

        if (entry.ExpiresAt <= now || !WebViewTicketEndpoints.IsSafeRelativeTarget(entry.Target))
        {
            entry = null;
            return false;
        }

        return true;
    }

    private static string CreateBase64UrlToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var pair in _tickets)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _tickets.TryRemove(pair.Key, out _);
            }
        }
    }
}
