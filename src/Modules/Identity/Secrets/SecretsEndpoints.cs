using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Runtime.Orleans.Capabilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Callers;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Modules.Identity.Secrets;

internal static class SecretsEndpoints
{
    internal static void MapSecrets(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/secrets/{owner}", async (string owner, SecretInput body, AuthorityNeurons neurons, HttpContext http, CancellationToken ct) =>
        {
            if (HttpCaller.Current(http) is not { } caller)
            {
                return Results.Unauthorized();
            }

            if (http.RequestServices.GetService<IBrainCapabilityPolicy>() is { } policy)
            {
                var decision = await policy.EvaluateAsync(caller.Brain.Value, BrainCapability.DurableSecrets, body.Name, ct).ConfigureAwait(false);
                if (!decision.Allowed)
                {
                    return ClaimPromptResult.FromDecision(decision);
                }
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
        }).WithMetadata(GuestSurface.Door.Instance);
    }
}
