using System.Diagnostics;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Http;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Authority.Identity;

// The one place the session cookie is issued. Claims are the identity-owned names (principal, account, brain, role).
// When the host registered identity@1 sessions, the cookie's session id is the one that capability issued.
internal sealed class AccountSessionIssuer(IServiceProvider services) : DigitalBrain.Runtime.Http.ISessionIssuer
{
    public async Task<SessionIssued> IssueAsync(HttpContext http, SessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);
        SessionClaims.Validate(request);
        SessionIssued issued;
        if (services.GetService<IIdentitySessionPort>() is not null && services.GetService<ISessions>() is { } sessions)
        {
            // identity@1 Validate/Issue/End are privileged to the auth module. The cookie edge stamps that module for the call.
            using (AsAuthModule())
            {
                issued = await sessions.Issue(request, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            issued = new SessionIssued(Guid.NewGuid().ToString("N"), Expires(request));
        }
        var principal = SessionPrincipal.Create(request, issued, CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { ExpiresUtc = issued.ExpiresAt, IsPersistent = request.Lifetime is not null }).ConfigureAwait(false);
        await SignedInAsync(request.Member, http).ConfigureAwait(false);
        return issued;
    }

    public async Task EndAsync(HttpContext http, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (SessionPrincipal.SessionId(http.User) is { Length: > 0 } sid
            && services.GetService<IIdentitySessionPort>() is not null
            && services.GetService<ISessions>() is { } sessions)
        {
            try
            {
                using (AsAuthModule())
                {
                    await sessions.End(sid, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (UnauthorizedAccessException exception)
            {
                if (services.GetService<ILoggerFactory>()?.CreateLogger(typeof(AccountSessionIssuer)) is { } logger)
                {
                    logger.LogWarning(exception, "Failed to revoke identity session {SessionId} during sign-out.", sid);
                }
                else
                {
                    Trace.TraceWarning("Failed to revoke identity session '{0}' during sign-out: {1}", sid, exception.Message);
                }
            }
        }
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
    }

    // The module id ModuleDeclarations checks. Auth's manifest grants Issue, End and Validate.
    private IDisposable AsAuthModule()
    {
        var edge = services.GetRequiredService<EdgeAuthority>();
        return edge.Enter(edge.Module("auth", new BrainId("system")));
    }

    private static DateTimeOffset Expires(SessionRequest request)
        => DateTimeOffset.UtcNow + (request.Lifetime is { } lifetime && lifetime > TimeSpan.Zero ? lifetime : TimeSpan.FromDays(14));

    private static async Task SignedInAsync(Member member, HttpContext http)
    {
        var handlers = http.RequestServices.GetServices<ISignInHandler>().ToArray();
        if (handlers.Length == 0) { return; }
        using var signedIn = http.RequestServices.GetRequiredService<EdgeAuthority>().Enter(
            AccountSession.ForMember(http.RequestServices.GetRequiredService<EdgeAuthority>(), member));
        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ISignInHandler));
        foreach (var handler in handlers)
        {
            try { await handler.SignedIn(member, http.RequestAborted); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "Sign-in handler {Handler} failed for {Principal}", handler.GetType().Name, member.PrincipalId); }
        }
    }
}
