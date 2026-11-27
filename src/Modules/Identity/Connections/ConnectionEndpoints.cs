using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Connectors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using DigitalBrain.Runtime.Http;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Connections;

namespace IntoChat.OS.Modules.Identity.Connections;

internal sealed record ConnectInput(string? ConnectorId, string? AuthMethodId, string? Value);
internal sealed record LoginInput(string? ConnectorId, string? AuthMethodId, string? Target);
internal sealed record DeviceStartInput(string? ConnectorId, string? AuthMethodId, string? Target);
internal sealed record DevicePollInput(string? FlowId);
internal sealed record ConnectionErrorResponse(string Error);
internal sealed record LoginUrlResponse(string Url);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConnectInput))]
[JsonSerializable(typeof(LoginInput))]
[JsonSerializable(typeof(DeviceStartInput))]
[JsonSerializable(typeof(DevicePollInput))]
[JsonSerializable(typeof(ConnectionErrorResponse))]
[JsonSerializable(typeof(LoginUrlResponse))]
[JsonSerializable(typeof(OAuthDeviceStartResult))]
[JsonSerializable(typeof(OAuthDevicePollResult))]
internal sealed partial class ConnectionEndpointsJsonContext : JsonSerializerContext;

internal static class ConnectionEndpoints
{
    internal static void MapConnectionNeurons(this IEndpointRouteBuilder endpoints)
    {
        var group = BrainRouteGroups.Brain(endpoints, "/connections").WithMetadata(GuestSurface.Door.Instance);

        group.MapGet("", async (AuthorityNeurons neurons, [FromServices] IReadOnlyList<ConnectorDefinition> connectors) =>
        {
            var scope = BrainScope.CurrentId();
            var rows = new List<ConnectionView>();
            var catalog = await neurons.Connections(scope).Read();
            foreach (var key in catalog.Rows.Select(r => r.Key))
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
            if (await RequireConnectorAllowedAsync(context, input.ConnectorId, ct) is { } claimPrompt) { return claimPrompt; }
            // The connector names the grain type, so an undeclared one would address no neuron at all.
            if (!connectors.Any(connector => connector.Id == input.ConnectorId))
            { return BadRequestError($"No composed module declares connector '{input.ConnectorId}'."); }
            if (await ConnectionLogin.UnavailableError(context.RequestServices, input.ConnectorId, input.AuthMethodId, ct) is { } unavailable) { return Conflict(unavailable); }
            var key = ResolveTargetKey(BrainScope.CurrentId(), input.ConnectorId, connectionId);
            // A declared connector may ship no connection neuron; the manifest check refuses it by name.
            try { return Results.Ok(await neurons.CallerConnection<IConnection>(context.RequestServices, key).Connect(input.AuthMethodId, input.Value)); }
            catch (ArgumentException error) { return BadRequestError(error.Message); }
        });

        group.MapPost("/{connectionId}/login", async (string connectionId, LoginInput input, HttpContext context, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.ConnectorId) || string.IsNullOrWhiteSpace(input.AuthMethodId)) { return Results.BadRequest(); }
            if (await RequireConnectorAllowedAsync(context, input.ConnectorId, ct) is { } claimPrompt) { return claimPrompt; }
            var start = await ConnectionLogin.StartAsync(context.RequestServices, BrainScope.CurrentId(), connectionId, input.ConnectorId, input.AuthMethodId, input.Target, context.Request, ct);
            return start.Url is { } url
                ? Results.Json(new LoginUrlResponse(url.AbsoluteUri), ConnectionEndpointsJsonContext.Default.LoginUrlResponse)
                : start.Conflict ? Conflict(start.Error ?? "Connector is unavailable.")
                : BadRequestError(start.Error ?? "Invalid login request.");
        });

        group.MapPost("/{connectionId}/device/start", async (string connectionId, DeviceStartInput input, HttpContext context, AuthorityNeurons neurons, [FromServices] IReadOnlyList<ConnectorDefinition> connectors, [FromServices] IOAuthDeviceFlow deviceFlow, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.ConnectorId) || string.IsNullOrWhiteSpace(input.AuthMethodId)) { return Results.BadRequest(); }
            if (await RequireConnectorAllowedAsync(context, input.ConnectorId, ct) is { } claimPrompt) { return claimPrompt; }
            if (!connectors.Any(connector => connector.Id == input.ConnectorId))
            { return BadRequestError($"No composed module declares connector '{input.ConnectorId}'."); }
            if (await ConnectionLogin.UnavailableError(context.RequestServices, input.ConnectorId, input.AuthMethodId, ct) is { } unavailable) { return Conflict(unavailable); }

            var brainId = BrainScope.CurrentId();
            var key = ResolveTargetKey(brainId, input.ConnectorId, connectionId);
            try
            {
                _ = neurons.CallerConnection<IConnection>(context.RequestServices, key);
                var result = await deviceFlow.StartAsync(new BrainId(brainId), key, input.AuthMethodId, input.Target, ct);
                return Results.Json(result, ConnectionEndpointsJsonContext.Default.OAuthDeviceStartResult);
            }
            catch (ClaimRequiredException error) { return ClaimPromptResult.FromDecision(error.Decision); }
            catch (ArgumentException error) { return BadRequestError(error.Message); }
            catch (InvalidOperationException error) { return Conflict(error.Message); }
        });

        group.MapPost("/{connectionId}/device/poll", async (string connectionId, DevicePollInput input, HttpContext context, [FromServices] IOAuthDeviceFlow deviceFlow, CancellationToken ct) =>
        {
            _ = connectionId;
            if (string.IsNullOrWhiteSpace(input.FlowId)) { return Results.BadRequest(); }
            if (await RequireConnectorAllowedAsync(context, connectorId: null, ct) is { } claimPrompt) { return claimPrompt; }

            var brainId = BrainScope.CurrentId();
            try
            {
                var result = await deviceFlow.PollOnceAsync(new BrainId(brainId), input.FlowId, ct);
                return Results.Json(result, ConnectionEndpointsJsonContext.Default.OAuthDevicePollResult);
            }
            catch (ClaimRequiredException error) { return ClaimPromptResult.FromDecision(error.Decision); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException error) { return BadRequestError(error.Message); }
            catch (InvalidOperationException error) { return Conflict(error.Message); }
        });

        group.MapPost("/{connectionId}/probe", async (string connectionId, HttpContext context, AuthorityNeurons neurons, CancellationToken ct) =>
        {
            var connectorId = context.Request.Query["connectorId"].FirstOrDefault();
            var connectionKey = context.Request.Query["connectionKey"].FirstOrDefault();
            if (await RequireConnectorAllowedAsync(context, connectorId, ct) is { } claimPrompt) { return claimPrompt; }
            var keys = await Tracked(neurons, connectionId, connectorId, connectionKey);
            return keys.Length switch
            {
                0 => Results.NotFound(),
                1 => Results.Ok(await neurons.Connection(keys[0]).Probe()),
                _ => Ambiguous(connectionId),
            };
        });

        group.MapPost("/{connectorId}/{connectionId}/probe", async (string connectorId, string connectionId, HttpContext context, AuthorityNeurons neurons, CancellationToken ct) =>
        {
            if (await RequireConnectorAllowedAsync(context, connectorId, ct) is { } claimPrompt) { return claimPrompt; }
            var keys = await Tracked(neurons, connectionId, connectorId);
            return keys.Length switch
            {
                0 => Results.NotFound(),
                1 => Results.Ok(await neurons.Connection(keys[0]).Probe()),
                _ => Ambiguous(connectionId),
            };
        });

        group.MapPost("/{connectionId}/disconnect", async (string connectionId, HttpContext context, AuthorityNeurons neurons, CancellationToken ct) =>
        {
            var connectorId = context.Request.Query["connectorId"].FirstOrDefault();
            var connectionKey = context.Request.Query["connectionKey"].FirstOrDefault();
            if (await RequireConnectorAllowedAsync(context, connectorId, ct) is { } claimPrompt) { return claimPrompt; }
            var keys = await Tracked(neurons, connectionId, connectorId, connectionKey);
            if (keys.Length != 1) { return keys.Length == 0 ? Results.NotFound() : Ambiguous(connectionId); }
            await neurons.Connection(keys[0]).Disconnect();
            return Results.Ok();
        });

        group.MapPost("/{connectorId}/{connectionId}/disconnect", async (string connectorId, string connectionId, HttpContext context, AuthorityNeurons neurons, CancellationToken ct) =>
        {
            if (await RequireConnectorAllowedAsync(context, connectorId, ct) is { } claimPrompt) { return claimPrompt; }
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
        var catalog = await neurons.Connections(scope).Read();
        var all = catalog.Rows.Select(r => r.Key);
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

    private static async Task<IResult?> RequireConnectorAllowedAsync(HttpContext context, string? connectorId, CancellationToken ct)
    {
        var policy = context.RequestServices.GetService<IBrainCapabilityPolicy>();
        if (policy is not null)
        {
            var decision = await policy.EvaluateAsync(BrainScope.CurrentId(), BrainCapability.ExternalConnector, connectorId, ct);
            if (!decision.Allowed)
            {
                return ClaimPromptResult.FromDecision(decision);
            }
        }

        if (DigitalBrain.Runtime.Http.Surface.HttpCaller.Current(context) is { } caller && GuestSurface.Is(caller))
        {
            return ClaimPromptResult.FromDecision(CapabilityDecision.RequireClaim(
                BrainCapability.ExternalConnector,
                string.IsNullOrWhiteSpace(connectorId)
                    ? "Sign in to claim your workspace and manage external connections."
                    : $"Sign in to claim your workspace and manage {connectorId}.",
                connectorId));
        }

        return null;
    }

    private static IResult BadRequestError(string error)
        => Results.Json(new ConnectionErrorResponse(error), ConnectionEndpointsJsonContext.Default.ConnectionErrorResponse, statusCode: StatusCodes.Status400BadRequest);

    private static IResult Conflict(string error)
        => Results.Json(new ConnectionErrorResponse(error), ConnectionEndpointsJsonContext.Default.ConnectionErrorResponse, statusCode: StatusCodes.Status409Conflict);

    // When a connectionId is shared by multiple connectors and no connectorId/connectionKey is specified, ask for disambiguation.
    private static IResult Ambiguous(string connectionId)
        => Conflict($"Connection '{connectionId}' names more than one connector in this brain; specify connectorId or a 3-part connectionKey.");
}
