using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Framework;
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
            var key = ResolveTargetKey(BrainScope.CurrentId(), input.ConnectorId, connectionId);
            // A declared connector may ship no connection neuron; the manifest check refuses it by name.
            try { return Results.Ok(await neurons.CallerConnection<IConnection>(context.RequestServices, key).Connect(input.AuthMethodId, input.Value)); }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
        });

        group.MapPost("/{connectionId}/login", async (string connectionId, LoginInput input, HttpContext context, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.ConnectorId) || string.IsNullOrWhiteSpace(input.AuthMethodId)) { return Results.BadRequest(); }
            var start = await ConnectionLogin.StartAsync(context.RequestServices, BrainScope.CurrentId(), connectionId, input.ConnectorId, input.AuthMethodId, input.Target, context.Request, ct);
            return start.Url is { } url ? Results.Ok(new { url = url.AbsoluteUri })
                : start.Conflict ? Results.Json(new { error = start.Error }, statusCode: StatusCodes.Status409Conflict)
                : Results.BadRequest(new { error = start.Error });
        });

        group.MapPost("/{connectionId}/probe", async (string connectionId, HttpContext context, AuthorityNeurons neurons) =>
        {
            var connectorId = context.Request.Query["connectorId"].FirstOrDefault();
            var connectionKey = context.Request.Query["connectionKey"].FirstOrDefault();
            var keys = await Tracked(neurons, connectionId, connectorId, connectionKey);
            return keys.Length switch
            {
                0 => Results.NotFound(),
                1 => Results.Ok(await neurons.Connection(keys[0]).Probe()),
                _ => Ambiguous(connectionId),
            };
        });

        group.MapPost("/{connectorId}/{connectionId}/probe", async (string connectorId, string connectionId, AuthorityNeurons neurons) =>
        {
            var keys = await Tracked(neurons, connectionId, connectorId);
            return keys.Length switch
            {
                0 => Results.NotFound(),
                1 => Results.Ok(await neurons.Connection(keys[0]).Probe()),
                _ => Ambiguous(connectionId),
            };
        });

        group.MapPost("/{connectionId}/disconnect", async (string connectionId, HttpContext context, AuthorityNeurons neurons) =>
        {
            var connectorId = context.Request.Query["connectorId"].FirstOrDefault();
            var connectionKey = context.Request.Query["connectionKey"].FirstOrDefault();
            var keys = await Tracked(neurons, connectionId, connectorId, connectionKey);
            if (keys.Length != 1) { return keys.Length == 0 ? Results.NotFound() : Ambiguous(connectionId); }
            await neurons.Connection(keys[0]).Disconnect();
            return Results.Ok();
        });

        group.MapPost("/{connectorId}/{connectionId}/disconnect", async (string connectorId, string connectionId, AuthorityNeurons neurons) =>
        {
            var keys = await Tracked(neurons, connectionId, connectorId);
            if (keys.Length != 1) { return keys.Length == 0 ? Results.NotFound() : Ambiguous(connectionId); }
            await neurons.Connection(keys[0]).Disconnect();
            return Results.Ok();
        });
    }

    internal static string ResolveTargetKey(string brainScope, string connectorId, string connectionIdOrKey)
    {
        var unescaped = Uri.UnescapeDataString(connectionIdOrKey);
        if (ConnectionKeys.TryParse(unescaped, out var parsed)
            && string.Equals(parsed.BrainId, brainScope, StringComparison.Ordinal)
            && string.Equals(parsed.ConnectorId, connectorId, StringComparison.Ordinal))
        {
            return ConnectionKeys.For(parsed.BrainId, parsed.ConnectorId, parsed.ConnectionId);
        }
        return ConnectionKeys.For(brainScope, connectorId, connectionIdOrKey);
    }

    internal static async Task<string[]> Tracked(AuthorityNeurons neurons, string connectionId, string? connectorId = null, string? connectionKey = null, string? scopeOverride = null)
    {
        var scope = scopeOverride ?? BrainScope.CurrentId();
        var all = await neurons.Connections(scope).List();
        return FilterTrackedKeys(all, scope, connectionId, connectorId, connectionKey);
    }

    public static string[] FilterTrackedKeys(IEnumerable<string> trackedKeys, string brainScope, string connectionId, string? connectorId = null, string? connectionKey = null)
    {
        ArgumentNullException.ThrowIfNull(trackedKeys);
        if (!string.IsNullOrWhiteSpace(connectionKey) && ConnectionKeys.TryParse(connectionKey, out var explicitKey))
        {
            var canonical = ConnectionKeys.For(explicitKey.BrainId, explicitKey.ConnectorId, explicitKey.ConnectionId);
            return string.Equals(explicitKey.BrainId, brainScope, StringComparison.Ordinal)
                ? [.. trackedKeys.Where(key => string.Equals(key, canonical, StringComparison.Ordinal))]
                : [];
        }

        var unescaped = Uri.UnescapeDataString(connectionId);
        if (ConnectionKeys.TryParse(unescaped, out var parsedId))
        {
            var canonical = ConnectionKeys.For(parsedId.BrainId, parsedId.ConnectorId, parsedId.ConnectionId);
            return string.Equals(parsedId.BrainId, brainScope, StringComparison.Ordinal)
                ? [.. trackedKeys.Where(key => string.Equals(key, canonical, StringComparison.Ordinal))]
                : [];
        }

        return
        [
            .. trackedKeys.Where(key =>
            {
                var parsed = ConnectionKeys.Parse(key);
                if (!string.Equals(parsed.ConnectionId, connectionId, StringComparison.Ordinal)) { return false; }
                return string.IsNullOrWhiteSpace(connectorId) || string.Equals(parsed.ConnectorId, connectorId, StringComparison.Ordinal);
            })
        ];
    }

    internal static Task<bool> IsTakenByAnotherConnector(AuthorityNeurons neurons, string connectionId, string connectorId)
    {
        _ = neurons;
        _ = connectionId;
        _ = connectorId;
        return Task.FromResult(false);
    }

    private static IResult Conflict(string error) => Results.Json(new { error }, statusCode: StatusCodes.Status409Conflict);

    // When a connectionId is shared by multiple connectors and no connectorId/connectionKey is specified, ask for disambiguation.
    private static IResult Ambiguous(string connectionId)
        => Results.Json(new { error = $"Connection '{connectionId}' names more than one connector in this brain; specify connectorId or a 3-part connectionKey." }, statusCode: StatusCodes.Status409Conflict);
}
