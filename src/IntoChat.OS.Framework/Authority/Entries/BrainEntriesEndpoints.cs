using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Platform.Entries;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
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
        var group = OsBrainRoutes.Group(endpoints, "/entries");
        group.AddEndpointFilter(RequireOwnerAsync);

        group.MapGet("", async (string brainId, HttpContext http, AuthorityNeurons neurons) =>
            Results.Ok(await neurons.Entries(Scope(http, brainId)).List()));

        group.MapPut("/{name}", async (string brainId, string name, EntryInput input, HttpContext http, AuthorityNeurons neurons) =>
        {
            if (input.Kind is not { } kind || string.IsNullOrEmpty(input.Value)) { return Results.BadRequest(); }
            try { await neurons.Entries(Scope(http, brainId)).Put(name, kind, input.Value); }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
            return Results.NoContent();
        });

        group.MapDelete("/{name}", async (string brainId, string name, HttpContext http, AuthorityNeurons neurons) =>
        {
            await neurons.Entries(Scope(http, brainId)).Remove(name);
            return Results.NoContent();
        });
    }

    private static string Scope(HttpContext http, string brainId) => BrainScope.Create(HttpCaller.RequiredPerson(http).Account, brainId).Id;

    // Runs after the group's membership filter. The owner rule is BrainOwnerDispatch's: the directory
    // membership of the stamped user in this brain, under the same account, with the Owner role.
    private static async ValueTask<object?> RequireOwnerAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (HttpCaller.Current(http) is not { } caller) { return Results.Unauthorized(); }
        if (caller.Actor is not Actor.Person person || http.Request.RouteValues["brainId"] is not string brainId)
        { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        var identity = http.RequestServices.GetRequiredService<IIdentityClient>();
        var scope = BrainScope.Create(person.Account, brainId).Id;
        var member = await identity.Membership(scope, person.Principal).WaitAsync(http.RequestAborted);
        if (member is null || member.Role != MemberRole.Owner || member.AccountId != person.Account || member.PrincipalId != person.Principal)
        { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        return await next(context);
    }
}
