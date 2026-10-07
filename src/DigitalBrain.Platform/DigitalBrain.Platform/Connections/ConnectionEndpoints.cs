using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Platform.Connections.OAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace DigitalBrain.Platform.Connections;

internal sealed record ConnectInput(string? ConnectorId, string? AuthMethodId, string? Value);
internal sealed record LoginInput(string? ConnectorId, string? AuthMethodId, string? Target);

internal static class ConnectionEndpoints
{
    internal static void MapConnectionNeurons(this IEndpointRouteBuilder endpoints)
    {
        var group = BrainRoutes.Group(endpoints, "/connections");

        group.MapGet("", async (IGrainFactory grains) =>
        {
            var scope = BrainScope.CurrentId();
            var rows = new List<ConnectionView>();
            foreach (var key in await grains.GetGrain<IConnectionIndex>(scope).List())
            { rows.Add(await grains.GetGrain<IConnection>(key).Read()); }
            return Results.Ok(rows);
        });

        group.MapGet("/requests", async (IGrainFactory grains) =>
            Results.Ok(await grains.GetGrain<DigitalBrain.Integrations.IConnectionRequests>(BrainScope.CurrentId()).ListPending()));

        group.MapPost("/{connectionId}/connect", async (string connectionId, ConnectInput input, IGrainFactory grains) =>
        {
            if (string.IsNullOrWhiteSpace(input.ConnectorId) || string.IsNullOrWhiteSpace(input.AuthMethodId) || string.IsNullOrWhiteSpace(input.Value)) { return Results.BadRequest(); }
            try { return Results.Ok(await grains.GetGrain<IConnection>(ConnectionKeys.For(BrainScope.CurrentId(), input.ConnectorId, connectionId)).Connect(input.AuthMethodId, input.Value)); }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
        });

        group.MapPost("/{connectionId}/login", async (string connectionId, LoginInput input, [FromServices] OAuthLoginFlow flow, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.ConnectorId) || string.IsNullOrWhiteSpace(input.AuthMethodId)) { return Results.BadRequest(); }
            var key = ConnectionKeys.For(BrainScope.CurrentId(), input.ConnectorId, connectionId);
            try { return Results.Ok(new { url = (await flow.StartAsync(key, input.AuthMethodId, input.Target, CallerContextStamper.Require(), ct)).AbsoluteUri }); }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
            catch (InvalidOperationException error) { return Results.Json(new { error = error.Message }, statusCode: StatusCodes.Status409Conflict); }
        });

        group.MapPost("/{connectionId}/probe", async (string connectionId, IGrainFactory grains) =>
            await Find(grains, connectionId) is { } key ? Results.Ok(await grains.GetGrain<IConnection>(key).Probe()) : Results.NotFound());

        group.MapPost("/{connectionId}/disconnect", async (string connectionId, IGrainFactory grains) =>
        {
            if (await Find(grains, connectionId) is not { } key) { return Results.NotFound(); }
            await grains.GetGrain<IConnection>(key).Disconnect();
            return Results.Ok();
        });
    }

    // The connection id is unique per brain across connectors; the index knows the full key.
    private static async Task<string?> Find(IGrainFactory grains, string connectionId)
        => (await grains.GetGrain<IConnectionIndex>(BrainScope.CurrentId()).List())
            .FirstOrDefault(key => ConnectionKeys.Parse(key).ConnectionId == connectionId);
}
