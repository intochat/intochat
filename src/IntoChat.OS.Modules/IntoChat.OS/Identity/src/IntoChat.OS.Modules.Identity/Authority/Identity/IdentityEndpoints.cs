using DigitalBrain.Brains;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Runtime.Orleans.Capabilities;
using System.Security.Claims;
using DigitalBrain.Runtime.Http;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Cap = DigitalBrain.Capabilities.Identity;
using OsMember = IntoChat.OS.Framework.Modules.Platform.Identity.Member;

namespace IntoChat.OS.Authority.Identity;

// Password-authenticated accounts own independent default workspaces. Invitation membership
// and grants remain scoped to the owning account and workspace.
internal static class IdentityEndpoints
{
    private const string PrincipalClaim = Cap.SessionClaims.Principal;

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet(AccountSession.CheckPath, static () => Results.NoContent());
        if (routes.ServiceProvider.GetRequiredService<IOptions<IdentityHostOptions>>().Value.BuiltInPasswordEndpoints)
        {
            routes.MapPost("/identity/register", RegisterAsync);
            routes.MapPost("/identity/login", LoginAsync);
        }
        External.ExternalSignIn.Map(routes);
        routes.MapGet("/identity/session", Session);
        routes.MapPost("/identity/session/brain", SelectBrainAsync);
        routes.MapPost("/identity/logout", (Delegate)LogoutAsync);
        routes.MapPost("/identity/brains", CreateBrainAsync);
        routes.MapGet("/identity/brains", BrainsAsync);
        var grants = BrainRoutes.Group(routes, "/grants");
        grants.MapGet("", async (string brainId, [FromServices] IIdentityClient identity, CancellationToken ct) =>
            Results.Ok(await identity.ListGrants(BrainScope.Create(CallerContextStamper.Require().AccountId, brainId).Id).WaitAsync(ct)));
        grants.MapPost("", async (string brainId, Grant input, [FromServices] IIdentityClient identity, CancellationToken ct) =>
        {
            try { return Results.Ok(await identity.GrantAsOwner(CallerContextStamper.Require() with { BrainId = brainId }, Grant.Normalize(input)).WaitAsync(ct)); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        });
        grants.MapPost("/revoke", async (string brainId, RevokeGrant input, [FromServices] IIdentityClient identity, CancellationToken ct) =>
        {
            try
            {
                var grantId = input.Id ?? input.GrantId;
                if (!string.IsNullOrEmpty(grantId))
                {
                    await identity.RevokeAsOwner(CallerContextStamper.Require() with { BrainId = brainId }, grantId).WaitAsync(ct);
                }
                else if (!string.IsNullOrEmpty(input.AppId) && !string.IsNullOrEmpty(input.SemanticTypeId))
                {
#pragma warning disable CS0618
                    await identity.RevokeAsOwner(CallerContextStamper.Require() with { BrainId = brainId }, input.AppId, input.SemanticTypeId, input.Mode).WaitAsync(ct);
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
        var account = http.User.FindFirstValue(Cap.SessionClaims.Account);
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
        if (AccountSession.ResolveCaller(http, auth.Value) is not { } caller) { return Results.Unauthorized(); }
        var brains = await identity.BrainsOf(caller.PrincipalId).WaitAsync(ct);
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
        return member is null ? Results.Unauthorized() : await IssueSessionAsync(member, http);
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
            var member = await passwords.Register(input.PrincipalId, input.Password ?? "", input.DisplayName ?? input.PrincipalId).WaitAsync(ct);
            return await IssueSessionAsync(member, http);
        }
        catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
        catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
    }

    private static async Task<IResult> IssueSessionAsync(OsMember member, HttpContext http, IReadOnlyCollection<Claim>? carried = null)
    {
        await http.RequestServices.GetRequiredService<ISessionIssuer>()
            .IssueAsync(http, SessionFor(member, carried), http.RequestAborted);
        return Results.Ok(member);
    }

    private static Cap.SessionRequest SessionFor(OsMember member, IReadOnlyCollection<Claim>? carried) => new()
    {
        Member = new Cap.Member
        {
            PrincipalId = member.PrincipalId,
            AccountId = member.AccountId,
            BrainId = member.BrainId,
            Role = (Cap.MemberRole)(int)member.Role,
            DisplayName = member.DisplayName,
            JoinedAt = member.JoinedAt,
        },
        Claims = carried is null
            ? new Dictionary<string, string>()
            : carried.Where(claim => !Cap.SessionClaims.IsIdentityOwned(claim.Type)).GroupBy(claim => claim.Type, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal),
    };

    // A brain switch keeps what a module put on the caller's session (an Auth session id, for one) so that module can
    // still end it. The claims the issuer reserves are never copied: they come from the selected membership alone.
    private static Claim[] CarriedClaims(HttpContext http)
    {
        return [.. http.User.Claims.Where(claim => !Cap.SessionClaims.IsIdentityOwned(claim.Type) && claim.Type != SessionPrincipal.SessionIdClaim)
            .Select(claim => new Claim(claim.Type, claim.Value, claim.ValueType))];
    }

    // Every resolved caller learns its brain, whichever posture resolved it: the shell's
    // defaultBrainId must match what /mcp stamps. Only a caller nothing resolved answers 204.
    private static IResult Session(HttpContext http, IOptions<AuthOptions> auth)
    {
        var caller = AccountSession.ResolveCaller(http, auth.Value);
        return caller is null
            ? Results.NoContent()
            : Results.Ok(caller);
    }

    private static async Task<IResult> LogoutAsync(HttpContext http)
    {
        await http.RequestServices.GetRequiredService<ISessionIssuer>().EndAsync(http);
        return Results.NoContent();
    }

    internal sealed record LoginRequest(string PrincipalId, string? Password = null);
    internal sealed record BrainSelection(string AccountId, string BrainId);
    internal sealed record RegisterRequest(string PrincipalId, string? Password = null, string? DisplayName = null);
    internal sealed record RevokeGrant(string? Id = null, string? GrantId = null, string? AppId = null, string? SemanticTypeId = null, GrantMode Mode = GrantMode.Always);
    internal sealed record BrainView(string Id, string Kind);
}
