using IntoChat.OS.Framework;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Runtime.Orleans.Capabilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Callers;

namespace IntoChat.OS.Authority.Secrets;

internal static class SecretsEndpoints
{
    internal static void MapSecrets(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/secrets/{owner}", async (string owner, SecretInput body, AuthorityNeurons neurons, HttpContext http) =>
        {
            if (HttpCaller.Current(http) is not { } caller)
            {
                return Results.Unauthorized();
            }

            // The owner, or an agent for the owner; an app never stores into a vault here.
            if (caller.Actor is not (Actor.Person or Actor.Agent) || caller.For?.Principal != owner)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.Value))
            {
                return Results.BadRequest();
            }

            var edge = http.RequestServices.GetRequiredService<EdgeAuthority>();
            var secret = await edge.AsKernel(BrainId.Kernel, () => neurons.Vault(owner).Set(body.Name, body.Label ?? body.Name, body.Value));
            return Results.Ok(secret);
        });
    }
}
