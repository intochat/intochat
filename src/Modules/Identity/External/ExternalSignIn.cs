using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Http.Surface;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Modules.Identity.External;

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
            return Results.Json(new { code = InvalidCode }, statusCode: StatusCodes.Status400BadRequest);
        }

        var claims = ExternalSignInTicket.Verify(http.Request.Query["t"].FirstOrDefault(), issuers.Of, clock);
        if (claims is null)
        {
            log.LogInformation("External sign-in refused: ticket did not verify");
            return Results.Json(new { code = InvalidCode }, statusCode: StatusCodes.Status400BadRequest);
        }

        bool fresh;
        // The replay ledger is the identity module's own: no person is stamped before the ticket is redeemed.
        using (neurons.Own())
        {
            fresh = await neurons.ExternalTicketReplay(claims.Issuer, claims.Jti).Consume(claims.Jti, claims.ExpiresAt).WaitAsync(http.RequestAborted);
        }
        if (!fresh)
        {
            log.LogWarning("External sign-in refused: ticket from {Issuer} was already redeemed", claims.Issuer);
            return Results.Json(new { code = InvalidCode }, statusCode: StatusCodes.Status400BadRequest);
        }

        var email = ExternalSignInTicket.NormalizeEmail(claims.Email)!;
        var principalId = PrincipalOf(claims.Issuer, email);
        var displayName = string.IsNullOrWhiteSpace(claims.Name) ? email : claims.Name.Trim();
        var claimService = http.RequestServices.GetService<BrainClaimService>();
        Member member;
        bool claimedInPlace = false;
        bool newBrain = false;
        if (claimService is not null && await claimService.TryClaimFromSessionAsync(http, principalId, displayName, http.RequestAborted) is { } claimed)
        {
            member = claimed;
            claimedInPlace = true;
        }
        else
        {
            var provisioning = http.RequestServices.GetRequiredService<IIdentityClient>();
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
            newBrain = provision.NewBrain;
            member = await births.Born(provision).WaitAsync(http.RequestAborted);
        }
        Claim[] session =
        [
            new(AmrClaim, claims.Amr),
            new(AuthTimeClaim, clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            new(IssuerClaim, claims.Issuer),
        ];
        await http.RequestServices.GetRequiredService<DigitalBrain.Runtime.Http.ISessionIssuer>()
            .IssueAsync(http, new SessionRequest
            {
                Member = member,
                Claims = session.ToDictionary(claim => claim.Type, claim => claim.Value, StringComparer.Ordinal),
            }, http.RequestAborted);
        log.LogInformation("External sign-in from {Issuer} for {Principal} ({Outcome})", claims.Issuer, principalId,
            claimedInPlace ? "claimed ephemeral brain in-place" : (newBrain ? "new brain" : "existing brain"));
        return Results.Redirect(returnUrl);
    }
}
