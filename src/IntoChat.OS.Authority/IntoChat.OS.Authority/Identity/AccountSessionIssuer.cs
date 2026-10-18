using System.Security.Claims;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Authority.Identity;

// The one place the session cookie is issued: login, register and select-brain, and any module that authenticates a member itself.
internal sealed class AccountSessionIssuer : ISessionIssuer
{
    public async Task<SessionIssued> IssueAsync(HttpContext http, SessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);
        var member = request.Member;
        var host = AccountSession.IdentityOf(http);
        string[] reserved = [ClaimTypes.NameIdentifier, ClaimTypes.Role, host.AccountClaim, host.BrainClaim];
        if (request.AdditionalClaims?.FirstOrDefault(claim => reserved.Contains(claim.Type, StringComparer.Ordinal)) is { } clash)
        { throw new ArgumentException($"Claim type '{clash.Type}' is set by the platform and cannot be supplied.", nameof(request)); }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, member.PrincipalId),
            new Claim(host.AccountClaim, member.AccountId),
            new Claim(host.BrainClaim, member.BrainId),
            new Claim(ClaimTypes.Role, member.Role.ToString()),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        if (request.AdditionalClaims is { } extra) { identity.AddClaims(extra); }

        DateTimeOffset? expires = null;
        if (request.Lifetime is { } lifetime)
        {
            expires = DateTimeOffset.UtcNow + lifetime;
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
                new AuthenticationProperties { ExpiresUtc = expires, IsPersistent = true });
        }
        else
        {
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        }
        await SignedInAsync(member, http);
        return new SessionIssued(member, expires);
    }

    public Task EndAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    // Modules react to a sign-in as the member who just proved who they are; none of them can fail it.
    private static async Task SignedInAsync(Member member, HttpContext http)
    {
        var handlers = http.RequestServices.GetServices<ISignInHandler>().ToArray();
        if (handlers.Length == 0) { return; }
        CallerContextStamper.Stamp(AccountSession.ForMember(member));
        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ISignInHandler));
        foreach (var handler in handlers)
        {
            try { await handler.SignedIn(member, http.RequestAborted); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "Sign-in handler {Handler} failed for {Principal}", handler.GetType().Name, member.PrincipalId); }
        }
    }
}
