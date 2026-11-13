using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Auth.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.Cloud.Auth.Http;

// The caller's own sessions, by handle; never a session id. Revoking one other session or every other session keeps
// the current one.
internal static class SessionEndpoints
{
    public const string Path = "/auth/sessions";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, ListAsync);
        endpoints.MapDelete(Path + "/{handle}", RevokeAsync);
        endpoints.MapPost(Path + "/revoke-others", RevokeOthersAsync);
    }

    private static async Task<IResult> RevokeAsync(string handle, HttpContext http, AuthCalls calls, SessionStarter starter, AuthAudit audit)
    {
        var caller = AuthCaller.Require(http);
        var rows = await calls.Sessions(caller.PrincipalId, table => table.List(starter.CurrentSidHash(http)));
        if (rows.FirstOrDefault(row => row.Handle == handle) is not { } row) { return AuthProblems.Code(StatusCodes.Status404NotFound, "NOT_FOUND"); }
        // The current device signs out; it is never revoked from the list.
        if (row.Current) { return AuthProblems.BadRequest(AuthProblems.RequestInvalid); }
        if (!await calls.Sessions(caller.PrincipalId, table => table.RevokeByHandle(handle))) { return AuthProblems.Code(StatusCodes.Status404NotFound, "NOT_FOUND"); }
        await audit.RecordAsync(caller.PrincipalId, AuthAuditKind.SessionRevoked, "success", http, "one");
        return Results.NoContent();
    }

    private static async Task<IResult> RevokeOthersAsync(HttpContext http, AuthCalls calls, SessionStarter starter, AuthAudit audit)
    {
        var caller = AuthCaller.Require(http);
        if (starter.CurrentSidHash(http) is not { } current) { return AuthProblems.BadRequest(AuthProblems.RequestInvalid); }
        await calls.Sessions(caller.PrincipalId, table => table.RevokeOthers(current));
        await audit.RecordAsync(caller.PrincipalId, AuthAuditKind.SessionRevoked, "success", http, "others");
        return Results.NoContent();
    }

    private static async Task<IResult> ListAsync(HttpContext http, AuthCalls calls, SessionStarter starter)
    {
        var caller = AuthCaller.Require(http);
        var current = starter.CurrentSidHash(http);
        var sessions = await calls.Sessions(caller.PrincipalId, table => table.List(current));
        return Results.Json(sessions.Select(session => new
        {
            handle = session.Handle,
            label = session.Label,
            createdAt = session.CreatedAt,
            lastSeenAt = session.LastSeenAt,
            current = session.Current,
        }));
    }
}
