using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Framework.Platform.Identity;
using IntoChat.OS.Sdk.Http;
using IntoChat.OS.Sdk.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Identity.External;

// Signs a person in on a trusted issuer's word: verify the ES256 ticket, redeem its jti once, provision the principal
// and its personal brain, start the platform session. Additive to the local password posture; mapped only when
// IntoChatOS:Auth:TrustedIssuers lists an issuer.
internal static class ExternalSignIn
{
    public const string Path = "/identity/external";
    public const string InvalidCode = "EXTERNAL_TICKET_INVALID";
    public const string PrincipalPrefix = "ext-";
    public const string AmrClaim = "amr";
    public const string AuthTimeClaim = "auth_time";
    public const string IssuerClaim = "idp";

    public static void Map(IEndpointRouteBuilder routes)
    {
        if (routes.ServiceProvider.GetService<TrustedIssuerKeys>() is not { Any: true }) { return; }
        routes.MapGet(Path, RedeemAsync).AllowAnonymous().AddEndpointFilter<IpThrottleFilter>();
    }

    // The issuer scopes the principal: two issuers vouching for one address sign in two principals. The prefix is
    // reserved, so no password registration can take a name an issuer will later sign in.
    public static string PrincipalOf(string issuer, string email)
        => PrincipalPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(issuer + "\n" + email)))[..32];

    private static async Task<IResult> RedeemAsync(HttpContext http, [FromServices] TrustedIssuerKeys issuers, [FromServices] AuthorityNeurons neurons,
        [FromServices] IBrainBirths births, [FromServices] TimeProvider clock, [FromServices] IOptions<AuthOptions> auth,
        [FromServices] ILoggerFactory logs)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        var log = logs.CreateLogger(typeof(ExternalSignIn));
        var returnUrl = http.Request.Query["returnUrl"].FirstOrDefault() ?? "/";
        if (!new ReturnUrlValidator(auth.Value.ExternalReturnOrigins).IsAllowed(returnUrl))
        {
            log.LogInformation("External sign-in refused: return URL not allowed");
            return HttpProblems.BadRequest(InvalidCode);
        }

        var claims = ExternalSignInTicket.Verify(http.Request.Query["t"].FirstOrDefault(), issuers.Of, clock);
        if (claims is null)
        {
            log.LogInformation("External sign-in refused: ticket did not verify");
            return HttpProblems.BadRequest(InvalidCode);
        }

        if (!await neurons.ExternalTicketReplay(claims.Issuer, claims.Jti).Consume(claims.Jti, claims.ExpiresAt).WaitAsync(http.RequestAborted))
        {
            log.LogWarning("External sign-in refused: ticket from {Issuer} was already redeemed", claims.Issuer);
            return HttpProblems.BadRequest(InvalidCode);
        }

        var email = ExternalSignInTicket.NormalizeEmail(claims.Email)!;
        var principalId = PrincipalOf(claims.Issuer, email);
        var displayName = string.IsNullOrWhiteSpace(claims.Name) ? email : claims.Name.Trim();
        var provisioning = http.RequestServices.GetRequiredKeyedService<IIdentityProvisioning>(PrivilegedModuleGate.HostKey);
        // Idempotent: a returning principal completes to its existing membership and brain; a new one registers once.
        BrainProvision provision;
        try
        {
            provision = await provisioning.CompleteRegistration(principalId).WaitAsync(http.RequestAborted)
                ?? await provisioning.RegisterPrincipal(principalId, displayName).WaitAsync(http.RequestAborted);
        }
        catch (InvalidOperationException)
        {
            // A concurrent first sign-in registered the principal between the two calls; complete to its membership.
            provision = await provisioning.CompleteRegistration(principalId).WaitAsync(http.RequestAborted)
                ?? throw new InvalidOperationException("External principal registration did not complete.");
        }
        var member = await births.Born(provision).WaitAsync(http.RequestAborted);
        Claim[] session =
        [
            new(AmrClaim, claims.Amr),
            new(AuthTimeClaim, clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            new(IssuerClaim, claims.Issuer),
        ];
        await http.RequestServices.GetRequiredKeyedService<ISessionIssuer>(PrivilegedModuleGate.HostKey)
            .IssueAsync(http, new SessionRequest(member, session), http.RequestAborted);
        log.LogInformation("External sign-in from {Issuer} for {Principal} ({NewBrain})", claims.Issuer, principalId, provision.NewBrain ? "new brain" : "existing brain");
        return Results.Redirect(returnUrl);
    }
}
