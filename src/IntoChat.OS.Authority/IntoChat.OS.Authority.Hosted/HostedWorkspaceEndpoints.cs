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
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Hosted;

internal static class HostedWorkspaceEndpoints
{
    internal sealed record AccessRequest(string UserId, string SessionId);
    internal sealed record RevokeRequest(string SessionId);
    internal sealed record ExchangeRequest(string Code);

    public static void Map(IEndpointRouteBuilder routes, RouteGroupBuilder management)
    {
        management.MapPost("/workspace", async (AccessRequest request, [FromServices] IOptions<HostedRuntimeOptions> options,
            [FromServices] IIdentityClient identity, [FromServices] HostedWorkspaceStore store, CancellationToken ct) =>
        {
            var scope = options.Value;
            var member = await identity.Membership(BrainScope.Create(scope.AccountId, scope.BrainId).Id, scope.AccountId).WaitAsync(ct);
            if (member?.Role != MemberRole.Owner) { return Results.Conflict(); }
            if (string.IsNullOrWhiteSpace(request.UserId) || request.UserId.Length > 200 ||
                string.IsNullOrWhiteSpace(request.SessionId) || request.SessionId.Length > 200) { return Results.BadRequest(); }
            return Results.Ok(await store.IssueAsync(request.UserId, request.SessionId, ct));
        });
        management.MapPost("/workspace/revoke", async (RevokeRequest request, [FromServices] HostedWorkspaceStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.SessionId) || request.SessionId.Length > 200) { return Results.BadRequest(); }
            await store.RevokeSessionAsync(request.SessionId, ct);
            return Results.NoContent();
        });
        routes.MapPost("/runtime/workspace/exchange", ExchangeAsync).AllowAnonymous();
        routes.MapGet("/runtime/workspace/launch", (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'";
            return Results.Content("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Workspace</title><p id=\"status\">Opening your workspace…</p><script src=\"/runtime/workspace/launch.js\"></script></html>", "text/html");
        }).AllowAnonymous();
        routes.MapGet("/runtime/workspace/launch.js", (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Content("""
                (async () => {
                  const code = location.hash.slice(1);
                  history.replaceState(null, '', location.pathname);
                  try {
                    if (!/^[A-Fa-f0-9]{64}$/.test(code)) throw new Error();
                    const response = await fetch('/runtime/workspace/exchange', {
                      method: 'POST', credentials: 'same-origin',
                      headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code })
                    });
                    if (!response.ok) throw new Error();
                    location.replace('/');
                  } catch {
                    document.getElementById('status').textContent = 'This workspace link expired or was already used. Open the workspace again from IntoChat Cloud.';
                  }
                })();
                """, "application/javascript");
        }).AllowAnonymous();
    }

    private static async Task<IResult> ExchangeAsync(ExchangeRequest request, HttpContext http,
        [FromServices] HostedWorkspaceStore store, [FromServices] IOptions<HostedRuntimeOptions> options,
        [FromServices] IOptions<IdentityHostOptions> identity, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var scope = options.Value;
        if (!scope.IsConfigured || !http.Request.IsHttps) { return Results.Unauthorized(); }
        var origin = http.Request.Headers.Origin;
        if (origin.Count != 1 || origin[0] != $"https://{http.Request.Host}") { return Results.StatusCode(403); }
        if (request.Code is null) { return Results.BadRequest(); }
        var grant = await store.ExchangeAsync(request.Code, ct);
        if (grant is null) { return Results.Unauthorized(); }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new(ClaimTypes.NameIdentifier, scope.AccountId), new(identity.Value.AccountClaim, scope.AccountId),
            new(identity.Value.BrainClaim, scope.BrainId), new(ClaimTypes.Role, MemberRole.Owner.ToString()),
            new(scope.GrantClaim, grant.Id), new(scope.RuntimeClaim, grant.RuntimeId),
            new(scope.CloudUserClaim, grant.UserId), new(scope.CloudSessionClaim, grant.SessionId)
        ], CookieAuthenticationDefaults.AuthenticationScheme));
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { ExpiresUtc = grant.ExpiresAt, AllowRefresh = false, IsPersistent = false });
        return Results.Ok(new { brainId = scope.BrainId });
    }

    public static async Task ValidateCookieAsync(CookieValidatePrincipalContext context)
    {
        var services = context.HttpContext.RequestServices;
        var scope = services.GetRequiredService<IOptions<HostedRuntimeOptions>>().Value;
        if (!scope.IsConfigured) { return; }
        var identity = services.GetRequiredService<IOptions<IdentityHostOptions>>().Value;
        var principal = context.Principal;
        var id = principal?.FindFirstValue(scope.GrantClaim);
        if (id is null || principal?.FindFirstValue(identity.AccountClaim) != scope.AccountId ||
            principal.FindFirstValue(identity.BrainClaim) != scope.BrainId || principal.FindFirstValue(scope.RuntimeClaim) != scope.RuntimeId ||
            !await services.GetRequiredService<HostedWorkspaceStore>().IsActiveAsync(id, context.HttpContext.RequestAborted))
        { context.RejectPrincipal(); }
    }

    public static void UseScope(WebApplication app)
    {
        var scope = app.Services.GetRequiredService<IOptions<HostedRuntimeOptions>>().Value;
        if (!scope.IsConfigured) { return; }
        app.Use(async (http, next) =>
        {
            var path = http.Request.Path;
            var route = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
            if (route is "/identity/register" or "/identity/login" or "/identity/session/brain" ||
                (route == "/identity/brains" && HttpMethods.IsPost(http.Request.Method)))
            { http.Response.StatusCode = 403; return; }
            if (http.Request.RouteValues.TryGetValue("brainId", out var brain) && brain?.ToString() != scope.BrainId)
            { http.Response.StatusCode = 403; return; }
            if (http.User.Identity?.IsAuthenticated != true &&
                http.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is null &&
                !path.StartsWithSegments("/health") && !path.StartsWithSegments("/alive") && !path.StartsWithSegments("/ready") &&
                !HttpMethods.IsOptions(http.Request.Method))
            { http.Response.StatusCode = 401; return; }
            if (http.User.FindFirstValue(scope.GrantClaim) is { } grantId)
            {
                var ticket = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                if (ticket.Properties?.ExpiresUtc is not { } expires) { http.Response.StatusCode = 401; return; }
                await HostedWorkspaceRequestLifetime.RunAsync(http, grantId, expires,
                    http.RequestServices.GetRequiredService<HostedWorkspaceStore>(),
                    http.RequestServices.GetRequiredService<TimeProvider>(), next);
            }
            else { await next(http); }
        });
    }
}
