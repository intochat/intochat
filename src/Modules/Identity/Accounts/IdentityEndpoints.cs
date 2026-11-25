using DigitalBrain.Access;
using System.Security.Claims;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Orleans.Capabilities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Host.Http;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Modules.Identity.Accounts;

// Password-authenticated accounts own independent default workspaces. Invitation membership
// and grants remain scoped to the owning account and workspace.
internal static class IdentityEndpoints
{
    private const string PrincipalClaim = SessionClaims.Principal;

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet(AccountSession.CheckPath, static () => Results.NoContent()).WithMetadata(GuestSurface.Door.Instance);
        routes.MapPost(PresenceProof.ExchangePath, ExchangeLocalPresence);
        if (routes.ServiceProvider.GetRequiredService<IOptions<IdentityHostOptions>>().Value.BuiltInPasswordEndpoints)
        {
            // Registration gives a visitor an owner account of their own, so it exists on loopback only (NetworkSignIn).
            if (NetworkSignInGuard.Configured(routes.ServiceProvider).OpenRegistration) { routes.MapPost("/identity/register", RegisterAsync); }
            routes.MapPost("/identity/login", LoginAsync);
        }
        IntoChat.OS.Modules.Identity.External.ExternalSignIn.Map(routes);
        IntoChat.OS.Modules.Identity.External.GuestSignIn.Map(routes);
        routes.MapGet("/identity/session", Session);
        routes.MapGet("/identity/residency", Residency);
        routes.MapPost("/identity/claim", ClaimAsync)
            .WithMetadata(GuestSurface.Door.Instance)
            .AddEndpointFilter<IpThrottleFilter>();
        routes.MapPost("/identity/session/brain", SelectBrainAsync);
        routes.MapPost("/identity/logout", LogoutAsync);
        routes.MapPost("/identity/brains", CreateBrainAsync);
        routes.MapGet("/identity/brains", BrainsAsync).WithMetadata(GuestSurface.Door.Instance);
        var grants = OsBrainRoutes.Group(routes, "/grants");
        grants.MapGet("", async (string brainId, HttpContext http, [FromServices] IIdentityClient identity, CancellationToken ct) =>
            Results.Ok(await identity.ListGrants(BrainScope.Create(HttpCaller.RequiredPerson(http).Account, brainId).Id).WaitAsync(ct)));
        grants.MapPost("", async (string brainId, Grant input, [FromServices] IIdentityClient identity, CancellationToken ct) =>
        {
            try { return Results.Ok(await identity.GrantAsOwner(brainId, Grant.Normalize(input)).WaitAsync(ct)); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        });
        grants.MapPost("/revoke", async (string brainId, RevokeGrant input, [FromServices] IIdentityClient identity, CancellationToken ct) =>
        {
            try
            {
                var grantId = input.Id ?? input.GrantId;
                if (!string.IsNullOrEmpty(grantId))
                {
                    await identity.RevokeAsOwner(brainId, grantId).WaitAsync(ct);
                }
                else if (!string.IsNullOrEmpty(input.AppId) && !string.IsNullOrEmpty(input.SemanticTypeId))
                {
#pragma warning disable CS0618
                    await identity.RevokeAsOwner(brainId, input.AppId, input.SemanticTypeId, input.Mode).WaitAsync(ct);
#pragma warning restore CS0618
                }
                return Results.NoContent();
            }
            catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        });
    }

    internal static async Task<IResult> CreateBrainAsync(HttpContext http, [FromServices] IIdentityClient identity, [FromServices] BrainBirths births, CancellationToken ct)
    {
        var principal = http.User.FindFirstValue(PrincipalClaim);
        var account = http.User.FindFirstValue(SessionClaims.Account);
        if (http.User.Identity?.IsAuthenticated != true || string.IsNullOrEmpty(principal) || string.IsNullOrEmpty(account))
        {
            return Results.Unauthorized();
        }
        var owner = await identity.ReadAccount(account).WaitAsync(ct);
        if (owner?.OwnerPrincipalId != principal) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        var operationId = http.Request.Headers["Idempotency-Key"].FirstOrDefault() ?? Guid.NewGuid().ToString("N");
        if (operationId.Length > 200) { return Results.BadRequest(new { error = "Idempotency-Key is too long." }); }
        var created = await births.Born(await identity.CreateBrain(account, principal, operationId, owner.Name, BrainKind.Personal).WaitAsync(ct));
        return Results.Ok(created);
    }

    // R126: the brains the signed-in principal is in, with their kind; the shell watches each circle brain's own /live.
    private static async Task<IResult> BrainsAsync(HttpContext http, [FromServices] IIdentityClient identity, IOptions<AuthOptions> auth, CancellationToken ct)
    {
        if (AccountSession.ResolveCaller(http, auth.Value)?.For is not { } person) { return Results.Unauthorized(); }
        var brains = await identity.BrainsOf(person.Principal).WaitAsync(ct);
        return Results.Ok(brains.Select(brain => new BrainView(brain.Id, brain.Kind.ToString())));
    }

    private static async Task<IResult> SelectBrainAsync(BrainSelection input, HttpContext http, [FromServices] IIdentityClient identity, CancellationToken ct)
    {
        var principal = http.User.FindFirstValue(PrincipalClaim);
        if (http.User.Identity?.IsAuthenticated != true || string.IsNullOrEmpty(principal))
        { return Results.Unauthorized(); }
        if (string.IsNullOrWhiteSpace(input.AccountId) || !BrainScope.IsValidId(input.BrainId))
        { return Results.BadRequest(); }
        var member = await identity.Membership(BrainScope.Create(input.AccountId, input.BrainId).Id, principal).WaitAsync(ct);
        return member is null ? Results.StatusCode(StatusCodes.Status403Forbidden) : await IssueSessionAsync(member, http, CarriedClaims(http));
    }

    private static async Task<IResult> LoginAsync(LoginRequest input, HttpContext http, [FromServices] PrincipalPasswords passwords, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.PrincipalId)) { return Results.Unauthorized(); }
        var member = await passwords.Authenticate(input.PrincipalId, input.Password ?? "").WaitAsync(ct);
        if (member is null) { return Results.Unauthorized(); }
        var claimService = http.RequestServices.GetService<BrainClaimService>();
        if (claimService is not null && await claimService.TryClaimFromSessionAsync(http, member.PrincipalId, member.DisplayName, ct) is { } claimed)
        {
            member = claimed;
        }
        return await IssueSessionAsync(member, http);
    }

    private static async Task<IResult> RegisterAsync(RegisterRequest input, HttpContext http, [FromServices] PrincipalPasswords passwords,
        IOptions<AuthOptions> auth, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.PrincipalId)) { return Results.BadRequest(new { error = "Username is required." }); }
        // The Basic bootstrap credential's name belongs to it.
        if (!string.IsNullOrEmpty(auth.Value.Username) && string.Equals(input.PrincipalId, auth.Value.Username, StringComparison.OrdinalIgnoreCase))
        { return Results.Conflict(new { error = "This username is reserved." }); }
        try
        {
            var claimService = http.RequestServices.GetService<BrainClaimService>();
            var ephemeralBrainId = claimService?.ActiveEphemeralBrainId(http);
            var member = ephemeralBrainId is not null
                ? await passwords.RegisterAndClaim(input.PrincipalId, input.Password ?? "", input.DisplayName ?? input.PrincipalId, ephemeralBrainId, claimService!, ct)
                : await passwords.Register(input.PrincipalId, input.Password ?? "", input.DisplayName ?? input.PrincipalId).WaitAsync(ct);
            return await IssueSessionAsync(member, http);
        }
        catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
        catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
    }

    private static async Task<IResult> ClaimAsync(ClaimBrainRequest input, HttpContext http, [FromServices] PrincipalPasswords passwords,
        IOptions<AuthOptions> auth, IOptions<IdentityHostOptions> identityOptions, CancellationToken ct)
    {
        var claimService = http.RequestServices.GetService<BrainClaimService>();
        if (claimService is null) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }

        // Only the ephemeral brain bound to the caller's verified HttpOnly cookie may be claimed; never trust a client-supplied brainId.
        var ephemeralBrainId = claimService.ActiveEphemeralBrainId(http);
        if (ephemeralBrainId is null)
        {
            return Results.Conflict(new { code = "NO_EPHEMERAL_BRAIN", error = "No active ephemeral brain is bound to this session." });
        }

        if (!identityOptions.Value.BuiltInPasswordEndpoints)
        {
            return Results.BadRequest(new { error = "Use external sign-in to claim this workspace." });
        }

        if (string.IsNullOrWhiteSpace(input.PrincipalId)) { return Results.BadRequest(new { error = "Username is required." }); }
        if (!string.IsNullOrEmpty(auth.Value.Username) && string.Equals(input.PrincipalId, auth.Value.Username, StringComparison.OrdinalIgnoreCase))
        { return Results.Conflict(new { error = "This username is reserved." }); }

        try
        {
            Member member;
            if (string.Equals(input.Mode, "login", StringComparison.OrdinalIgnoreCase))
            {
                var existing = await passwords.Authenticate(input.PrincipalId, input.Password ?? "").WaitAsync(ct);
                if (existing is null) { return Results.Unauthorized(); }
                member = await claimService.ClaimAsync(ephemeralBrainId, existing.PrincipalId, existing.DisplayName, ct);
            }
            else
            {
                if (!NetworkSignInGuard.Configured(http.RequestServices).OpenRegistration)
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
                member = await passwords.RegisterAndClaim(input.PrincipalId, input.Password ?? "", input.DisplayName ?? input.PrincipalId, ephemeralBrainId, claimService, ct);
            }

            await http.RequestServices.GetRequiredService<ISessionIssuer>()
                .IssueAsync(http, SessionFor(member, carried: null), http.RequestAborted);
            var snapshot = SnapshotFor(http, member.BrainId, member.PrincipalId);
            return Results.Ok(new
            {
                principalId = member.PrincipalId,
                accountId = member.AccountId,
                brainId = member.BrainId,
                displayName = member.DisplayName,
                claimed = true,
                residency = FormatResidency(snapshot),
            });
        }
        catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
        catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
        catch (UnauthorizedAccessException error) { return Results.Json(new { error = error.Message }, statusCode: StatusCodes.Status403Forbidden); }
    }

    private static async Task<IResult> IssueSessionAsync(Member member, HttpContext http, IReadOnlyCollection<Claim>? carried = null)
    {
        await http.RequestServices.GetRequiredService<ISessionIssuer>()
            .IssueAsync(http, SessionFor(member, carried), http.RequestAborted);
        return Results.Ok(member);
    }

    private static SessionRequest SessionFor(Member member, IReadOnlyCollection<Claim>? carried) => new()
    {
        Member = member,
        Claims = carried is null
            ? new Dictionary<string, string>()
            : carried.Where(claim => !SessionClaims.IsIdentityOwned(claim.Type)).GroupBy(claim => claim.Type, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal),
    };

    // A brain switch keeps what a module put on the caller's session (an Auth session id, for one) so that module can
    // still end it. The claims the issuer reserves are never copied: they come from the selected membership alone.
    private static Claim[] CarriedClaims(HttpContext http)
    {
        return [.. http.User.Claims.Where(claim => !SessionClaims.IsIdentityOwned(claim.Type) && claim.Type != SessionPrincipal.SessionIdClaim)
            .Select(claim => new Claim(claim.Type, claim.Value, claim.ValueType))];
    }

    // Every resolved caller learns its brain and residency metadata, whichever posture resolved it: the shell's
    // defaultBrainId must match what /mcp stamps, and Slice OSS-3's Top Guest Pill binds directly to residency.
    // Only a caller nothing resolved answers 204.
    private static IResult Session(HttpContext http, IOptions<AuthOptions> auth)
    {
        var caller = AccountSession.ResolveCaller(http, auth.Value);
        if (caller?.For is not { } person) { return Results.NoContent(); }
        var snapshot = SnapshotFor(http, caller.Brain.Value, person.Principal);
        return Results.Ok(new
        {
            principalId = person.Principal,
            accountId = person.Account,
            brainId = caller.Brain.Value,
            appId = caller.Actor is Actor.App app ? app.Key : null,
            conversationId = caller.Conversation,
            residency = FormatResidency(snapshot),
        });
    }

    private static IResult Residency(HttpContext http, IOptions<AuthOptions> auth)
    {
        var caller = AccountSession.ResolveCaller(http, auth.Value);
        if (caller?.For is not { } person) { return Results.NoContent(); }
        var snapshot = SnapshotFor(http, caller.Brain.Value, person.Principal);
        return Results.Ok(new
        {
            brainId = caller.Brain.Value,
            isEphemeral = snapshot.IsEphemeral,
            ttlSeconds = snapshot.TtlSeconds,
            remainingTurns = snapshot.RemainingTurns,
            memoryUsedBytes = snapshot.MemoryUsedBytes,
            memoryLimitBytes = snapshot.MemoryLimitBytes,
            ownerPrincipalId = snapshot.OwnerPrincipalId,
        });
    }

    private static BrainResidencySnapshot SnapshotFor(HttpContext http, string brainId, string principalId)
    {
        var registry = http.RequestServices?.GetService<IBrainResidencyRegistry>();
        if (registry is not null)
        {
            var snap = registry.GetSnapshot(brainId);
            if (!snap.IsEphemeral && snap.OwnerPrincipalId is null && !GuestSurface.IsGuestPrincipal(principalId))
            {
                return snap with { OwnerPrincipalId = principalId };
            }
            return snap;
        }
        var storage = http.RequestServices?.GetService<IBrainResidencyStorage>();
        return BrainResidencySnapshot.Persistent(
            ownerPrincipalId: GuestSurface.IsGuestPrincipal(principalId) ? null : principalId,
            memoryUsedBytes: storage?.GetMemoryUsedBytes(brainId) ?? 0);
    }

    private static object FormatResidency(BrainResidencySnapshot snapshot) => new
    {
        isEphemeral = snapshot.IsEphemeral,
        ttlSeconds = snapshot.TtlSeconds,
        remainingTurns = snapshot.RemainingTurns,
        memoryUsedBytes = snapshot.MemoryUsedBytes,
        memoryLimitBytes = snapshot.MemoryLimitBytes,
        ownerPrincipalId = snapshot.OwnerPrincipalId,
    };

    private static IResult ExchangeLocalPresence(LocalPresenceRequest input, HttpContext http, IOptions<AuthOptions> auth)
    {
        if (auth.Value.Posture != IdentityPosture.Open
            || !PresenceProofValidator.IsSameOriginRequest(http.Request)
            || http.RequestServices.GetService<IPresenceProof>() is not { } proof
            || !proof.MatchesHeader(input.Token))
        {
            return Results.Unauthorized();
        }

        http.Response.Cookies.Append(
            PresenceProof.CookieName,
            proof.IssueCookieValue(),
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = http.Request.IsHttps,
                Path = "/",
                IsEssential = true,
            });
        return Results.Ok(new { ok = true });
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, [FromServices] ISessionIssuer issuer)
    {
        http.Response.Cookies.Delete(PresenceProof.CookieName, new CookieOptions { Path = "/", SameSite = SameSiteMode.Strict });
        await issuer.EndAsync(http);
        return Results.NoContent();
    }

    internal sealed record LocalPresenceRequest(string? Token = null);
    internal sealed record LoginRequest(string PrincipalId, string? Password = null);
    internal sealed record BrainSelection(string AccountId, string BrainId);
    internal sealed record RegisterRequest(string PrincipalId, string? Password = null, string? DisplayName = null);
    internal sealed record ClaimBrainRequest(string PrincipalId, string? Password = null, string? DisplayName = null, string? Mode = null);
    internal sealed record RevokeGrant(string? Id = null, string? GrantId = null, string? AppId = null, string? SemanticTypeId = null, GrantMode Mode = GrantMode.Always);
    internal sealed record BrainView(string Id, string Kind);
}
