using System.Globalization;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Identity.External;

// Guest try on this install (owner decision D3): a trusted issuer listed with Guests sends an anonymous visitor here with a guest
// ticket. The install verifies it, spends its jti once, makes the guest's account and brain (the issuer-scoped try- principal,
// GuestSurface.PrincipalOf) and starts a session that lasts until the ticket's stay ends. The session is a guest's: it passes only
// the endpoints marked with GuestSurface.Door (AccountSession), never a person's.
internal static class GuestSignIn
{
    public const string Path = "/identity/external/guest";
    public const string GuestMethod = "guest";
    private const string LedgerPrefix = "guest:";

    public static void Map(IEndpointRouteBuilder routes)
    {
        if (routes.ServiceProvider.GetService<TrustedIssuerKeys>() is not { AnyGuests: true }) { return; }
        routes.MapGet(Path, RedeemAsync).AllowAnonymous().AddEndpointFilter<IpThrottleFilter>();
    }

    private static async Task<IResult> RedeemAsync(HttpContext http, [FromServices] TrustedIssuerKeys issuers, [FromServices] AuthorityNeurons neurons,
        [FromServices] IIdentityClient identity, [FromServices] IBrainBirths births, [FromServices] TimeProvider clock,
        [FromServices] IOptions<AuthOptions> auth, [FromServices] ILoggerFactory logs)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        var log = logs.CreateLogger(typeof(GuestSignIn));
        var invalid = Results.Json(new { code = ExternalSignIn.InvalidCode }, statusCode: StatusCodes.Status400BadRequest);
        var returnUrl = http.Request.Query["returnUrl"].FirstOrDefault() ?? "/";
        if (!new ReturnUrlValidator(auth.Value.ExternalReturnOrigins).IsAllowed(returnUrl))
        {
            log.LogInformation("Guest sign-in refused: return URL not allowed");
            return invalid;
        }

        if (GuestTicket.Verify(http.Request.Query["t"].FirstOrDefault(), issuers.GuestsOf, clock) is not { } claims)
        {
            log.LogInformation("Guest sign-in refused: ticket did not verify");
            return invalid;
        }

        bool fresh;
        using (neurons.Own())
        {
            fresh = await neurons.ExternalTicketReplay(LedgerPrefix + claims.Issuer, claims.Jti).Consume(claims.Jti, claims.ExpiresAt).WaitAsync(http.RequestAborted);
        }
        if (!fresh)
        {
            log.LogWarning("Guest sign-in refused: ticket from {Issuer} was already redeemed", claims.Issuer);
            return invalid;
        }

        var now = clock.GetUtcNow();
        var guest = GuestSurface.PrincipalOf(claims.Issuer, claims.Session);
        // Idempotent: a returning visitor of the same try session gets the same guest and brain.
        await identity.EnsureAccount(new Account { AccountId = guest, OwnerPrincipalId = guest, Name = "Guest", CreatedAt = now }).WaitAsync(http.RequestAborted);
        var provision = await identity.EnsureBrain(new Member
        {
            PrincipalId = guest,
            AccountId = guest,
            BrainId = guest,
            Role = MemberRole.Owner,
            DisplayName = "Guest",
            JoinedAt = now,
        }).WaitAsync(http.RequestAborted);
        var member = await births.Born(provision).WaitAsync(http.RequestAborted);
        await http.RequestServices.GetRequiredService<DigitalBrain.Runtime.Http.ISessionIssuer>()
            .IssueAsync(http, new SessionRequest
            {
                Member = member,
                Claims = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ExternalSignIn.AmrClaim] = GuestMethod,
                    [ExternalSignIn.AuthTimeClaim] = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                    [ExternalSignIn.IssuerClaim] = claims.Issuer,
                },
                Lifetime = claims.Until - now,
            }, http.RequestAborted);
        log.LogInformation("Guest sign-in from {Issuer} as {Guest} until {Until}", claims.Issuer, guest, claims.Until);
        return Results.Redirect(returnUrl);
    }
}
