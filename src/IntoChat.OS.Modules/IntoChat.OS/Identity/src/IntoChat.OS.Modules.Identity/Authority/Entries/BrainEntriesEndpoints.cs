using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Platform.Entries;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Entries;

internal sealed record EntryInput(EntryKind? Kind, string? Value);

// Owner-only: /brains/{brainId}/entries. There is no release route; secret values never leave over HTTP.
internal static class BrainEntriesEndpoints
{
    internal static void MapBrainEntries(this IEndpointRouteBuilder endpoints)
    {
        var group = BrainRoutes.Group(endpoints, "/entries");
        group.AddEndpointFilter(RequireOwnerAsync);

        group.MapGet("", async (string brainId, AuthorityNeurons neurons) =>
            Results.Ok(await neurons.Entries(Scope(brainId)).List()));

        group.MapPut("/{name}", async (string brainId, string name, EntryInput input, AuthorityNeurons neurons) =>
        {
            if (input.Kind is not { } kind || string.IsNullOrEmpty(input.Value)) { return Results.BadRequest(); }
            try { await neurons.Entries(Scope(brainId)).Put(name, kind, input.Value); }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
            return Results.NoContent();
        });

        group.MapDelete("/{name}", async (string brainId, string name, AuthorityNeurons neurons) =>
        {
            await neurons.Entries(Scope(brainId)).Remove(name);
            return Results.NoContent();
        });
    }

    private static string Scope(string brainId) => BrainScope.Create(CallerContextStamper.Require().AccountId, brainId).Id;

    // Runs after the group's membership filter. The owner rule is BrainOwnerDispatch's: the directory
    // membership of the stamped user in this brain, under the same account, with the Owner role.
    private static async ValueTask<object?> RequireOwnerAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!CallerContextStamper.TryGet(out var caller)) { return Results.Unauthorized(); }
        if (caller.Kind != CallerKind.User || http.Request.RouteValues["brainId"] is not string brainId)
        { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        var identity = http.RequestServices.GetRequiredService<IIdentityClient>();
        var scope = BrainScope.Create(caller.AccountId, brainId).Id;
        var member = await identity.Membership(scope, caller.PrincipalId).WaitAsync(http.RequestAborted);
        if (member is null || member.Role != MemberRole.Owner || member.AccountId != caller.AccountId || member.PrincipalId != caller.PrincipalId)
        { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        return await next(context);
    }
}
