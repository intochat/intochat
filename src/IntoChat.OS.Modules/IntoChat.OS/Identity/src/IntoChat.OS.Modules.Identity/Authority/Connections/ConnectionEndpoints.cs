using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Integrations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Authority.Connections;

internal sealed record ConnectInput(string? ConnectorId, string? AuthMethodId, string? Value);
internal sealed record LoginInput(string? ConnectorId, string? AuthMethodId, string? Target);

internal static class ConnectionEndpoints
{
    internal static void MapConnectionNeurons(this IEndpointRouteBuilder endpoints)
    {
        var group = BrainRoutes.Group(endpoints, "/connections");

        group.MapGet("", async (AuthorityNeurons neurons, [FromServices] IReadOnlyList<ConnectorDefinition> connectors) =>
        {
            var scope = BrainScope.CurrentId();
            var rows = new List<ConnectionView>();
            foreach (var key in await neurons.Connections(scope).List())
            {
                // A connector no longer composed has no neuron to read; its key is skipped, not fatal.
                if (!connectors.Any(connector => connector.Id == ConnectionKeys.Parse(key).ConnectorId)) { continue; }
                rows.Add(await neurons.Connection(key).Read());
            }
            return Results.Ok(rows);
        });

        group.MapGet("/requests", async (AuthorityNeurons neurons) =>
            Results.Ok(await neurons.ConnectionRequests(BrainScope.CurrentId()).ListPending()));

        group.MapPost("/{connectionId}/connect", async (string connectionId, ConnectInput input, HttpContext context, AuthorityNeurons neurons, [FromServices] IReadOnlyList<ConnectorDefinition> connectors, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.ConnectorId) || string.IsNullOrWhiteSpace(input.AuthMethodId) || string.IsNullOrWhiteSpace(input.Value)) { return Results.BadRequest(); }
            // The connector names the grain type, so an undeclared one would address no neuron at all.
            if (!connectors.Any(connector => connector.Id == input.ConnectorId))
            { return Results.BadRequest(new { error = $"No composed module declares connector '{input.ConnectorId}'." }); }
            if (await ConnectionLogin.UnavailableError(context.RequestServices, input.ConnectorId, ct) is { } unavailable) { return Conflict(unavailable); }
            if (await ConnectionLogin.TakenError(neurons, connectionId, input.ConnectorId) is { } taken) { return Conflict(taken); }
            // A declared connector may ship no connection neuron; the manifest check refuses it by name.
            try { return Results.Ok(await neurons.CallerConnection<IConnection>(context.RequestServices, ConnectionKeys.For(BrainScope.CurrentId(), input.ConnectorId, connectionId)).Connect(input.AuthMethodId, input.Value)); }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
        });

        group.MapPost("/{connectionId}/login", async (string connectionId, LoginInput input, HttpContext context, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.ConnectorId) || string.IsNullOrWhiteSpace(input.AuthMethodId)) { return Results.BadRequest(); }
            var start = await ConnectionLogin.StartAsync(context.RequestServices, BrainScope.CurrentId(), connectionId, input.ConnectorId, input.AuthMethodId, input.Target, ct);
            return start.Url is { } url ? Results.Ok(new { url = url.AbsoluteUri })
                : start.Conflict ? Results.Json(new { error = start.Error }, statusCode: StatusCodes.Status409Conflict)
                : Results.BadRequest(new { error = start.Error });
        });

        group.MapPost("/{connectionId}/probe", async (string connectionId, AuthorityNeurons neurons) =>
        {
            var keys = await Tracked(neurons, connectionId);
            return keys.Length switch
            {
                0 => Results.NotFound(),
                1 => Results.Ok(await neurons.Connection(keys[0]).Probe()),
                _ => Ambiguous(connectionId),
            };
        });

        group.MapPost("/{connectionId}/disconnect", async (string connectionId, AuthorityNeurons neurons) =>
        {
            var keys = await Tracked(neurons, connectionId);
            if (keys.Length != 1) { return keys.Length == 0 ? Results.NotFound() : Ambiguous(connectionId); }
            await neurons.Connection(keys[0]).Disconnect();
            return Results.Ok();
        });
    }

    private static async Task<string[]> Tracked(AuthorityNeurons neurons, string connectionId)
        => [.. (await neurons.Connections(BrainScope.CurrentId()).List())
            .Where(key => ConnectionKeys.Parse(key).ConnectionId == connectionId)];

    internal static async Task<bool> IsTakenByAnotherConnector(AuthorityNeurons neurons, string connectionId, string connectorId)
        => (await Tracked(neurons, connectionId)).Any(key => ConnectionKeys.Parse(key).ConnectorId != connectorId);

    private static IResult Conflict(string error) => Results.Json(new { error }, statusCode: StatusCodes.Status409Conflict);

    // Only a race between two connects can leave two keys; never pick one silently.
    private static IResult Ambiguous(string connectionId)
        => Results.Json(new { error = $"Connection '{connectionId}' names more than one connector in this brain." }, statusCode: StatusCodes.Status409Conflict);
}
