using System.Security.Claims;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Identity;
using IntoChat.OS.Authority.Identity.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Identity;

// Password-authenticated accounts own independent default workspaces. Invitation membership
// and grants remain scoped to the owning account and workspace.
internal static class IdentityEndpoints
{
    private const string PrincipalClaim = ClaimTypes.NameIdentifier;
    private const string RoleClaim = ClaimTypes.Role;

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet(AccountSession.CheckPath, static () => Results.NoContent());
        if (routes.ServiceProvider.GetRequiredService<IOptions<IdentityHostOptions>>().Value.BuiltInPasswordEndpoints)
        {
            routes.MapPost("/identity/register", RegisterAsync);
            routes.MapPost("/identity/login", LoginAsync);
        }
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
            try { return Results.Ok(await identity.GrantAsOwner(CallerContextStamper.Require() with { BrainId = brainId }, input).WaitAsync(ct)); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        });
        grants.MapPost("/revoke", async (string brainId, RevokeGrant input, [FromServices] IIdentityClient identity, CancellationToken ct) =>
        {
            try
            {
                await identity.RevokeAsOwner(CallerContextStamper.Require() with { BrainId = brainId }, input.AppId, input.SemanticTypeId, input.Mode).WaitAsync(ct);
                return Results.NoContent();
            }
            catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        });
    }

    internal static async Task<IResult> CreateBrainAsync(HttpContext http, [FromServices] IIdentityClient identity, [FromServices] BrainBirths births, CancellationToken ct)
    {
        var principal = http.User.FindFirstValue(PrincipalClaim);
        var account = http.User.FindFirstValue(AccountSession.IdentityOf(http).AccountClaim);
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

    private static async Task<IResult> IssueSessionAsync(Member member, HttpContext http, IReadOnlyCollection<Claim>? carried = null)
    {
        var issued = await http.RequestServices.GetRequiredKeyedService<ISessionIssuer>(PrivilegedModuleGate.HostKey).IssueAsync(http, new SessionRequest(member, carried), http.RequestAborted);
        return Results.Ok(issued.Member);
    }

    // A brain switch keeps what a module put on the caller's session (an Auth session id, for one) so that module can
    // still end it. The claims the issuer reserves are never copied: they come from the selected membership alone.
    private static Claim[] CarriedClaims(HttpContext http)
    {
        var host = AccountSession.IdentityOf(http);
        string[] reserved = [ClaimTypes.NameIdentifier, ClaimTypes.Role, host.AccountClaim, host.BrainClaim];
        return [.. http.User.Claims.Where(claim => !reserved.Contains(claim.Type, StringComparer.Ordinal)).Select(claim => new Claim(claim.Type, claim.Value, claim.ValueType))];
    }

    // Every resolved caller learns its brain, whichever posture resolved it: the shell's
    // defaultBrainId must match what /mcp stamps. Only a caller nothing resolved answers 204.
    private static IResult Session(HttpContext http, IOptions<AuthOptions> auth)
    {
        var caller = AccountSession.ResolveCaller(http, auth.Value);
        return caller is null
            ? Results.NoContent()
            : Results.Ok(new SessionView(
                caller.PrincipalId,
                caller.AccountId,
                caller.BrainId,
                http.User.Identity?.IsAuthenticated == true ? http.User.FindFirstValue(RoleClaim) ?? "" : ""));
    }

    private static async Task<IResult> LogoutAsync(HttpContext http)
    {
        await http.RequestServices.GetRequiredKeyedService<ISessionIssuer>(PrivilegedModuleGate.HostKey).EndAsync(http);
        return Results.NoContent();
    }

    internal sealed record LoginRequest(string PrincipalId, string? Password = null);
    internal sealed record BrainSelection(string AccountId, string BrainId);
    internal sealed record RegisterRequest(string PrincipalId, string? Password = null, string? DisplayName = null);
    internal sealed record RevokeGrant(string AppId, string SemanticTypeId, GrantMode Mode);
    internal sealed record BrainView(string Id, string Kind);
    internal sealed record SessionView(string PrincipalId, string AccountId, string BrainId, string Role);
}
