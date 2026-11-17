using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Access;
using Microsoft.Extensions.DependencyInjection;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Modules.Circles;

// D-03/D-10: an owner or admin mints a circle invite code. The code is in this response only; redeem is in-process
// (ICircleInviteRedeemer, the cloud enrollment module), not an endpoint here.
public sealed partial class CirclesModule
{
    internal sealed record MintCodeRequest(int? ValidForMinutes = null, string? Role = null, bool BindCaller = false);

    private static void MapInviteCodes(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/circle-invitations/{brainId}/codes", async (string brainId, MintCodeRequest? body, HttpContext http, IServiceProvider plain) =>
        {
            var edge = plain.GetRequiredService<EdgeAuthority>();
            if (http.User.Identity?.IsAuthenticated != true || HttpCaller.Current(http) is not { Actor: Actor.Person person, Direct: true } caller)
            { return Results.Unauthorized(); }
            if (!BrainScope.IsValidId(brainId)) { return Results.BadRequest(); }
            var request = new MintInvite(TimeSpan.FromMinutes(body?.ValidForMinutes ?? 24 * 60), body?.Role ?? "member", body?.BindCaller ?? false);
            if (request.Role is not ("member" or "admin")) { return Results.BadRequest(); }
            using var there = edge.Enter(edge.Person(person.Principal, person.Account, new DigitalBrain.BrainId(brainId), caller.Conversation));
            try
            {
                var minted = await SiloNeurons.Get<ICircleInvites>(plain, ICircleInvites.Key(brainId)).Mint(request);
                http.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new { code = minted.Code, expiresAt = minted.ExpiresAt });
            }
            catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            catch (InvalidOperationException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            catch (KeyNotFoundException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        });
    }
}
