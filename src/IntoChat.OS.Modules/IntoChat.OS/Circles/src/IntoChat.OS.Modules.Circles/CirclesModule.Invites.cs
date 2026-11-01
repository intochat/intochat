using DigitalBrain.Capabilities;
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
    public sealed record MintCodeRequest(int? ValidForMinutes = null, string? Role = null, bool BindCaller = false);

    private static void MapInviteCodes(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/circle-invitations/{brainId}/codes", async (string brainId, MintCodeRequest? body, HttpContext http, IServiceProvider plain) =>
        {
            if (http.User.Identity?.IsAuthenticated != true || !CallerContextStamper.TryGet(out var caller)
                || caller.Kind != CallerKind.User || caller.StampedBy != TrustedEdge.AuthenticatedHttp)
            { return Results.Unauthorized(); }
            if (!BrainScope.IsValidId(brainId)) { return Results.BadRequest(); }
            var request = new MintInvite(TimeSpan.FromMinutes(body?.ValidForMinutes ?? 24 * 60), body?.Role ?? "member", body?.BindCaller ?? false);
            if (request.Role is not ("member" or "admin")) { return Results.BadRequest(); }
            CallerContextStamper.Stamp(caller with { BrainId = brainId });
            try
            {
                var minted = await SiloNeurons.Get<ICircleInvites>(plain, ICircleInvites.Key(brainId)).Mint(request);
                http.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new { code = minted.Code, expiresAt = minted.ExpiresAt });
            }
            catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            catch (InvalidOperationException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            catch (KeyNotFoundException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            finally { CallerContextStamper.Stamp(caller); }
        });
    }
}
