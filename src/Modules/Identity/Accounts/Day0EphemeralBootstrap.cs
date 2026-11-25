using System.Globalization;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Identity.Accounts;

/// <summary>
/// Day-0 instant boot middleware: when <see cref="AuthOptions.Day0EphemeralTry"/> is enabled, an unauthenticated
/// browser visitor opening <c>GET /</c> (or <c>/auth/check</c>, <c>/identity/session</c>, <c>/identity/residency</c>)
/// automatically receives an <c>HttpOnly</c>, <c>SameSite=Lax</c> (and <c>Secure</c> on HTTPS) ephemeral session cookie
/// bound to a fresh in-memory <c>Ephemeral</c> brain, landing directly in the OS desktop with zero login wall.
/// </summary>
public static class Day0EphemeralBootstrap
{
    public const string AmrValue = "ephemeral";
    internal const string MintedPrincipalItemKey = "IntoChat.OS.Day0EphemeralBootstrap.MintedPrincipal";
    private const string MiddlewareAppliedKey = "IntoChat.OS.Day0EphemeralBootstrap.Applied";

    internal sealed class MintThrottle(IRequestThrottle inner)
    {
        public async ValueTask<bool> AllowAsync(string key, CancellationToken ct)
            => (await inner.HitAsync(key, ct).ConfigureAwait(false)).Allowed;
    }

    public static WebApplication UseDay0EphemeralBootstrap(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!((IApplicationBuilder)app).Properties.TryAdd(MiddlewareAppliedKey, true)) { return app; }

        var auth = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
        if (!auth.Day0EphemeralTry) { return app; }

        app.Use(async (context, next) =>
        {
            if (ShouldBootstrap(context.Request))
            {
                var continuePipeline = await TryMintIfUnauthenticatedAsync(context).ConfigureAwait(false);
                if (!continuePipeline) { return; }
            }
            await next(context).ConfigureAwait(false);
        });

        return app;
    }

    private static bool ShouldBootstrap(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method)) { return false; }

        // Never hijack explicit Authorization (Basic/Bearer) or Open-posture local presence callers.
        if (request.Headers.Authorization.Count > 0) { return false; }
        if (request.Headers.ContainsKey(PresenceProof.HeaderName) || request.Cookies.ContainsKey(PresenceProof.CookieName)) { return false; }

        var path = request.Path.Value;
        return string.Equals(path, "/", StringComparison.Ordinal)
            || string.Equals(path, "/index.html", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, AccountSession.CheckPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "/identity/session", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "/identity/residency", StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<bool> TryMintIfUnauthenticatedAsync(HttpContext context)
    {
        var services = context.RequestServices;
        var ephemeral = services.GetService<EphemeralBrainRegistry>();
        var births = services.GetService<IBrainBirths>();
        var issuer = services.GetService<ISessionIssuer>();
        if (ephemeral is null || births is null || issuer is null) { return true; }

        // Authenticate cookie explicitly because this middleware runs before UseStaticWebShell & UseAuthentication.
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            var result = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            if (result.Succeeded && result.Principal is not null)
            {
                user = result.Principal;
                context.User = user;
            }
        }

        if (user.Identity?.IsAuthenticated == true)
        {
            var existingPrincipal = SessionPrincipal.PrincipalId(user);
            var existingBrain = user.FindFirst(SessionClaims.Brain)?.Value ?? existingPrincipal;
            if (!string.IsNullOrWhiteSpace(existingPrincipal))
            {
                // Keep any durable session or still-live ephemeral session untouched.
                if (!GuestSurface.IsGuestPrincipal(existingPrincipal))
                {
                    return true;
                }
                if (!string.IsNullOrWhiteSpace(existingBrain) && ephemeral.Contains(existingBrain))
                {
                    ephemeral.Touch(existingBrain);
                    return true;
                }
                // Check if this try-* brain was promoted or persisted externally.
                var identity = services.GetService<IIdentityClient>();
                if (identity is not null && !string.IsNullOrWhiteSpace(existingBrain)
                    && await identity.BrainExists(existingBrain).WaitAsync(context.RequestAborted).ConfigureAwait(false))
                {
                    return true;
                }
                // Otherwise the ephemeral brain expired/evicted — seamlessly mint a fresh one below.
            }
        }

        // Rate-limit ephemeral mints per IP prefix (/24 IPv4 or /64 IPv6).
        var throttle = services.GetService<MintThrottle>();
        if (throttle is not null && !await throttle.AllowAsync("ephemeral:" + IpPrefix.Truncate(context.Connection.RemoteIpAddress?.ToString()), context.RequestAborted).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return false;
        }

        var record = ephemeral.Mint();
        await births.BornEphemeral(record.Owner, record.Residency).WaitAsync(context.RequestAborted).ConfigureAwait(false);

        var clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        var now = clock.GetUtcNow();
        var request = new SessionRequest
        {
            Member = record.Owner,
            Claims = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["amr"] = AmrValue,
                ["auth_time"] = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            },
            Lifetime = record.Residency.IdleTtl,
        };
        await issuer.IssueAsync(context, request, context.RequestAborted).ConfigureAwait(false);

        // Stamp context.User and stash in Items so UseAuthentication cannot overwrite it with a stale evicted cookie.
        var mintedUser = AccountSession.SessionUser(
            services.GetRequiredService<DigitalBrain.Access.EdgeAuthority>()
                .Guest(record.Owner.PrincipalId, record.Owner.AccountId, new DigitalBrain.BrainId(record.BrainId)));
        context.User = mintedUser;
        context.Items[MintedPrincipalItemKey] = mintedUser;

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(Day0EphemeralBootstrap));
        logger?.LogInformation("Day-0 ephemeral brain {BrainId} minted in RAM", record.BrainId);
        return true;
    }
}
