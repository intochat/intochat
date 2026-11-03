using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Connections.Platform.Secrets;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Authority.Secrets;

internal static class SecretsEndpoints
{
    internal static void MapSecrets(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/secrets/{owner}", async (string owner, SecretInput body, AuthorityNeurons neurons) =>
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

            var secret = await neurons.Vault(owner).Set(caller, body.Name, body.Label ?? body.Name, body.Value);
            return Results.Ok(secret);
        });
    }
}
