using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Diagnostics;
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
        if (services.GetService<ISessions>() is { } sessions)
        {
            // identity@1 Validate/Issue/End are privileged to the auth module. The cookie edge stamps that module for the call.
            using (RuntimeCallerContext.Stamp(AuthModuleCaller))
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
        if (SessionPrincipal.SessionId(http.User) is { Length: > 0 } sid && services.GetService<ISessions>() is { } sessions)
        {
            try
            {
                using (RuntimeCallerContext.Stamp(AuthModuleCaller))
                {
                    await sessions.End(sid, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (UnauthorizedAccessException exception)
            {
                services.GetService<ILoggerFactory>()?.CreateLogger(typeof(AccountSessionIssuer))
                    .LogSwallowed(exception, "session end");
            }
        }
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
    }

    // AppId is the module id ModuleDeclarations checks. Auth's manifest grants Issue, End and Validate.
    private static readonly CapabilityCaller AuthModuleCaller = new("auth", "auth", "system", "auth");

    private static DateTimeOffset Expires(SessionRequest request)
        => DateTimeOffset.UtcNow + (request.Lifetime is { } lifetime && lifetime > TimeSpan.Zero ? lifetime : TimeSpan.FromDays(14));

    private static async Task SignedInAsync(Member member, HttpContext http)
    {
        var handlers = http.RequestServices.GetServices<ISignInHandler>().ToArray();
        if (handlers.Length == 0) { return; }
        CallerContextStamper.Stamp(CapabilityCallers.Of(member.PrincipalId, member.AccountId, member.BrainId));
        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ISignInHandler));
        foreach (var handler in handlers)
        {
            try { await handler.SignedIn(member, http.RequestAborted); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "Sign-in handler {Handler} failed for {Principal}", handler.GetType().Name, member.PrincipalId); }
        }
    }
}
