using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Authority.Connections.OAuth;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Authority.Integrations;

internal static class IntegrationCatalogEndpoints
{
    internal static void MapIntegrations(this IEndpointRouteBuilder endpoints)
    {
        // The shell picks from this list, so a connector no connection neuron serves is not offered to it.
        endpoints.MapGet("/connectors", async (HttpContext context, [FromServices] ConnectorCatalog catalog, CancellationToken cancellationToken) =>
            RuntimeCallerContext.TryGet(out _)
                ? Results.Ok((await catalog.ListAsync(cancellationToken)).Where(entry => ConnectionGrains.IsConnector(context.RequestServices, entry.Id)).ToArray())
                : Results.Unauthorized());

        // Inbound webhooks for external providers resolve the target connection neuron via 3-part ConnectionKeys.
        endpoints.MapPost("/integrations/{provider}/webhook", async (string provider, HttpContext context) =>
        {
            await OAuthCallback.HandleWebhookAsync(context, provider);
            return Results.Empty;
        });
        endpoints.MapPost("/integrations/{provider}", async (string provider, HttpContext context) =>
        {
            await OAuthCallback.HandleWebhookAsync(context, provider);
            return Results.Empty;
        });

        // Deliberately not brain-scoped: registrations are deployment info, not workspace data.
        var group = endpoints.MapGroup("/integrations");
        group.AddEndpointFilter(async (context, next) =>
            RuntimeCallerContext.TryGet(out _) ? await next(context) : Results.Unauthorized());

        group.MapGet("", async ([FromServices] IReadOnlyList<IntegrationSchema> definitions, [FromServices] AuthorityNeurons neurons,
            [FromServices] Microsoft.Extensions.Options.IOptions<IntoChat.OS.Connections.Identity.Configuration.AuthOptions> auth, CancellationToken cancellationToken) =>
            IntegrationOperatorGate.Allows(RuntimeCallerContext.TryGet(out var caller) ? caller : null, auth.Value)
                ? Results.Ok(await IntegrationCatalog.ListAsync(definitions, neurons, cancellationToken))
                : Results.StatusCode(StatusCodes.Status403Forbidden));

        group.MapPost("/{id}/registration", async (string id, ConfigureRegistrationInput body, [FromServices] Microsoft.Extensions.Options.IOptions<IntoChat.OS.Connections.Identity.Configuration.AuthOptions> auth,
            [FromServices] IReadOnlyList<IntegrationSchema> definitions, [FromServices] AuthorityNeurons neurons) =>
        {
            if (!IntegrationOperatorGate.Allows(RuntimeCallerContext.TryGet(out var caller) ? caller : null, auth.Value)) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            if (!definitions.Any(definition => definition.Id == id)) { return Results.NotFound(); }
            try
            {
                var registration = neurons.Integration(id);
                return Results.Ok(await registration.Configure(new ConfigureIntegration { Values = body.Values ?? [] }));
            }
            catch (ArgumentException error)
            {
                return Results.BadRequest(new { error = error.Message });
            }
        });

        group.MapDelete("/{id}/registration/{field}", async (string id, string field, [FromServices] Microsoft.Extensions.Options.IOptions<IntoChat.OS.Connections.Identity.Configuration.AuthOptions> auth,
            [FromServices] IReadOnlyList<IntegrationSchema> definitions, [FromServices] AuthorityNeurons neurons) =>
        {
            if (!IntegrationOperatorGate.Allows(RuntimeCallerContext.TryGet(out var caller) ? caller : null, auth.Value)) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            if (!definitions.Any(definition => definition.Id == id)) { return Results.NotFound(); }
            try
            {
                return Results.Ok(await neurons.Integration(id).Clear(field));
            }
            catch (ArgumentException error)
            {
                return Results.BadRequest(new { error = error.Message });
            }
        });
    }
}
