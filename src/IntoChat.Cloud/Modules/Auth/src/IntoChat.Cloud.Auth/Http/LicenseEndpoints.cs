using IntoChat.Cloud.Auth.Licensing;
using IntoChat.Cloud.Auth.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.Cloud.Auth.Http;

// GET /auth/license: installation-wide licence state for operators only (R2). Anyone else, and everyone while the
// operator list is empty, gets 404. Never the token.
internal static class LicenseEndpoints
{
    public const string Path = "/auth/license";

    public static void Map(IEndpointRouteBuilder endpoints) => endpoints.MapGet(Path, GetAsync);

    private static async Task<IResult> GetAsync(HttpContext http, AuthModuleOptions options, AuthLicenseState state, AuthCalls calls, TimeProvider clock)
    {
        var caller = AuthCaller.Require(http);
        if (!options.Operators.Contains(caller.PrincipalId, StringComparer.Ordinal))
        {
            return Results.NotFound();
        }

        var (holder, expiresAt, status) = state.Read();
        var expiry = expiresAt ?? DateTimeOffset.MinValue;
        var daysLeft = expiresAt is null ? 0 : Math.Max(0, (int)Math.Floor((expiry - clock.GetUtcNow()).TotalDays));
        var history = await calls.SystemAudit(audit => audit.Read(AuthAuditKind.LicenseState, 20));
        return Results.Json(new
        {
            license = new AuthLicenseView(holder, expiry, daysLeft, status),
            history = history.Select(entry => new { at = entry.At, outcome = entry.Outcome, detail = entry.Detail }),
        });
    }
}
