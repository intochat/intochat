using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Authority.Connections.OAuth;
using DigitalBrain.Authority.Integrations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace DigitalBrain.Authority.Connections;

internal sealed record ConnectInput(string? ConnectorId, string? AuthMethodId, string? Value);
internal sealed record LoginInput(string? ConnectorId, string? AuthMethodId, string? Target);

internal static class ConnectionEndpoints
{
    internal static void MapConnectionNeurons(this IEndpointRouteBuilder endpoints)
    {
        var group = BrainRoutes.Group(endpoints, "/connections");

        group.MapGet("", async (IGrainFactory grains, [FromServices] IReadOnlyList<ConnectorDefinition> connectors) =>
        {
            var scope = BrainScope.CurrentId();
            var rows = new List<ConnectionView>();
            foreach (var key in await grains.GetGrain<IConnectionIndex>(scope).List())
            {
                // A connector no longer composed has no neuron to read; its key is skipped, not fatal.
                if (!connectors.Any(connector => connector.Id == ConnectionKeys.Parse(key).ConnectorId)) { continue; }
                rows.Add(await grains.GetConnection(key).Read());
            }
            return Results.Ok(rows);
        });

        group.MapGet("/requests", async (IGrainFactory grains) =>
            Results.Ok(await grains.GetGrain<DigitalBrain.Integrations.IConnectionRequests>(BrainScope.CurrentId()).ListPending()));

        group.MapPost("/{connectionId}/connect", async (string connectionId, ConnectInput input, HttpContext context, IGrainFactory grains, [FromServices] IReadOnlyList<ConnectorDefinition> connectors, [FromServices] ConnectorCatalog catalog, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.ConnectorId) || string.IsNullOrWhiteSpace(input.AuthMethodId) || string.IsNullOrWhiteSpace(input.Value)) { return Results.BadRequest(); }
            // The connector names the grain type, so an undeclared one would address no neuron at all.
            if (!connectors.Any(connector => connector.Id == input.ConnectorId))
            { return Results.BadRequest(new { error = $"No composed module declares connector '{input.ConnectorId}'." }); }
            if (await Unavailable(catalog, connectors, input.ConnectorId, ct) is { } unavailable) { return unavailable; }
            if (await TakenByAnotherConnector(grains, connectionId, input.ConnectorId) is { } taken) { return taken; }
            // A declared connector may ship no connection neuron; the manifest check refuses it by name.
            try { return Results.Ok(await grains.GetNeuronGrain<IConnection>(context.RequestServices, ConnectionKeys.For(BrainScope.CurrentId(), input.ConnectorId, connectionId)).Connect(input.AuthMethodId, input.Value)); }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
        });

        group.MapPost("/{connectionId}/login", async (string connectionId, LoginInput input, HttpContext context, [FromServices] IGrainFactory grains, [FromServices] OAuthLoginFlow flow, [FromServices] IReadOnlyList<ConnectorDefinition> connectors, [FromServices] ConnectorCatalog catalog, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.ConnectorId) || string.IsNullOrWhiteSpace(input.AuthMethodId)) { return Results.BadRequest(); }
            if (await Unavailable(catalog, connectors, input.ConnectorId, ct) is { } unavailable) { return unavailable; }
            if (await TakenByAnotherConnector(grains, connectionId, input.ConnectorId) is { } taken) { return taken; }
            var key = ConnectionKeys.For(BrainScope.CurrentId(), input.ConnectorId, connectionId);
            try
            {
                // The callback grants into this key's neuron; refuse now rather than after the provider round trip.
                _ = grains.GetNeuronGrain<IConnection>(context.RequestServices, key);
                return Results.Ok(new { url = (await flow.StartAsync(key, input.AuthMethodId, input.Target, CallerContextStamper.Require(), ct)).AbsoluteUri });
            }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
            catch (InvalidOperationException error) { return Results.Json(new { error = error.Message }, statusCode: StatusCodes.Status409Conflict); }
        });

        group.MapPost("/{connectionId}/probe", async (string connectionId, IGrainFactory grains) =>
        {
            var keys = await Tracked(grains, connectionId);
            return keys.Length switch
            {
                0 => Results.NotFound(),
                1 => Results.Ok(await grains.GetConnection(keys[0]).Probe()),
                _ => Ambiguous(connectionId),
            };
        });

        group.MapPost("/{connectionId}/disconnect", async (string connectionId, IGrainFactory grains) =>
        {
            var keys = await Tracked(grains, connectionId);
            if (keys.Length != 1) { return keys.Length == 0 ? Results.NotFound() : Ambiguous(connectionId); }
            await grains.GetConnection(keys[0]).Disconnect();
            return Results.Ok();
        });
    }

    // Probe and disconnect name a connection by id alone, so connect and login refuse an id the brain
    // already tracks under another connector; the index then holds at most one key per id.
    private static async Task<string[]> Tracked(IGrainFactory grains, string connectionId)
        => [.. (await grains.GetGrain<IConnectionIndex>(BrainScope.CurrentId()).List())
            .Where(key => ConnectionKeys.Parse(key).ConnectionId == connectionId)];

    internal static async Task<bool> IsTakenByAnotherConnector(IGrainFactory grains, string connectionId, string connectorId)
        => (await Tracked(grains, connectionId)).Any(key => ConnectionKeys.Parse(key).ConnectorId != connectorId);

    private static async Task<IResult?> TakenByAnotherConnector(IGrainFactory grains, string connectionId, string connectorId)
        => await IsTakenByAnotherConnector(grains, connectionId, connectorId)
            ? Results.Json(new { error = $"Connection '{connectionId}' already belongs to another connector in this brain." }, statusCode: StatusCodes.Status409Conflict)
            : null;

    // A declared connector whose required Integrations the deployment has not made Ready is hidden by
    // the catalog; it takes no new connection either. Names the connector only, never a field value.
    private static async Task<IResult?> Unavailable(ConnectorCatalog catalog, IReadOnlyList<ConnectorDefinition> connectors, string connectorId, CancellationToken ct)
        => connectors.Any(connector => connector.Id == connectorId) && !(await catalog.ListAsync(ct)).Any(entry => entry.Id == connectorId)
            ? Results.Json(new { error = $"Connector '{connectorId}' is not offered by this deployment." }, statusCode: StatusCodes.Status409Conflict)
            : null;

    // Only a race between two connects can leave two keys; never pick one silently.
    private static IResult Ambiguous(string connectionId)
        => Results.Json(new { error = $"Connection '{connectionId}' names more than one connector in this brain." }, statusCode: StatusCodes.Status409Conflict);
}
