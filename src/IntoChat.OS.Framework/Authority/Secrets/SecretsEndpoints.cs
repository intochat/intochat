using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Connections.Platform.Secrets;
using IntoChat.OS.Contracts.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Secrets;

internal static class SecretsEndpoints
{
    internal static void MapSecrets(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/secrets/{owner}", async (string owner, SecretInput body, AuthorityNeurons neurons, HttpContext http) =>
        {
            if (!RuntimeCallerContext.TryGet(out var caller))
            {
                return Results.Unauthorized();
            }

            if (caller.PrincipalId != owner)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.Value))
            {
                return Results.BadRequest();
            }

            var edge = http.RequestServices.GetService<EdgeAuthority>();
            using (edge?.Enter(edge.Module("identity.secrets", new BrainId("system"))))
            {
                var secret = await neurons.Vault(owner).Set(caller, body.Name, body.Label ?? body.Name, body.Value);
                return Results.Ok(secret);
            }
        });
    }
}
