using DigitalBrain.Brains;
using DigitalBrain.Runtime.Orleans.Capabilities;
using System.Reflection;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Authority.Hosting;

internal static class RuntimeManagementEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        // SessionGroup avoids the normal membership filter's create-on-access behavior.
        BrainRoutes.SessionGroup(endpoints, "/management/v1")
            .MapGet("/observation", ObserveAsync);
    }

    private static async Task<IResult> ObserveAsync(string brainId, HttpContext http,
        [FromServices] IIdentityClient identity, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        // A synthetic Open-posture caller is not an authenticated management session.
        if (http.User.Identity?.IsAuthenticated != true) { return Results.Unauthorized(); }
        var caller = RuntimeCallerContext.Require();
        var owner = await identity.OwnerOfBrain(brainId).WaitAsync(ct);
        if (owner is null || owner.AccountId != caller.AccountId || owner.OwnerPrincipalId != caller.PrincipalId)
        {
            return Results.NotFound();
        }
        var scope = BrainScope.Create(owner.AccountId, brainId).Id;
        if (await identity.Membership(scope, caller.PrincipalId).WaitAsync(ct) is null)
        {
            return Results.NotFound();
        }
        var version = typeof(RuntimeManagementEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        return Results.Ok(new RuntimeObservation(1, version, owner.AccountId, brainId,
            await identity.BrainExists(scope).WaitAsync(ct), ["observe"]));
    }
}
