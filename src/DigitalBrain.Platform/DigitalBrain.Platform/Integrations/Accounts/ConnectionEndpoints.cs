using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Platform.Integrations.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DigitalBrain.Platform.Integrations.Accounts;

internal static class ConnectionEndpoints
{
    internal static string Status(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Connected => "Configured",
        ConnectionStatus.Expired => "Expired",
        ConnectionStatus.Disconnected => "Disconnected",
        _ => "Unavailable",
    };

    internal static void MapConnections(this IEndpointRouteBuilder endpoints)
    {
        var group = BrainRoutes.Group(endpoints, "/connections");
        group.MapGet("", async (IGrainFactory grains, IEnumerable<IExternalConnection> external, CancellationToken cancellationToken) =>
        {
            var scope = CurrentScope();
            var rows = ScopedConnections.Visible(scope, await grains.GetGrain<IConnectionRegistry>(scope.Id).List(cancellationToken))
                .Select(Project).ToList();
            foreach (var account in external)
            {
                if (await account.ReadAsync(grains, cancellationToken) is { } row) { rows.Add(row); }
            }

            return Results.Ok(rows);
        });

        group.MapGet("/requests", async (IGrainFactory grains) =>
            Results.Ok(await grains.GetGrain<DigitalBrain.Integrations.IConnectionRequests>(CurrentScope().Id).ListPending()));

        group.MapPost("/connect", async (ConnectConnectionInput input, IGrainFactory grains, CancellationToken cancellationToken) =>
        {
            // The browser may supply a new value, never a reference into any vault.
            if (input.SecretReference is not null) { return Results.BadRequest(new { error = "Supply a connection value." }); }
            if (string.IsNullOrWhiteSpace(input.ConnectionId) || input.ConnectionId.StartsWith("oauth:", StringComparison.Ordinal)) { return Results.BadRequest(); }
            try
            {
                var request = new ConnectConnection
                {
                    IntegrationId = input.ConnectorId ?? "",
                    ConnectionId = input.ConnectionId,
                    Label = input.Label,
                    Value = input.Value,
                    AuthMethodId = input.AuthMethodId ?? "",
                };
                return Results.Ok(Project(await grains.GetGrain<IConnectionRegistry>(CurrentScope().Id).Connect(request, cancellationToken)));
            }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
        });

        group.MapPost("/probe", async (ConnectionAction input, IGrainFactory grains, IEnumerable<IExternalConnection> external, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(input.ConnectionId)) { return Results.BadRequest(); }
            if (external.FirstOrDefault(account => account.ConnectionId == input.ConnectionId) is { } oauth)
            { return await oauth.ReadAsync(grains, cancellationToken) is { } row ? Results.Ok(row) : Results.NotFound(); }
            try { return Results.Ok(Project(await Registry(grains).Probe(input.ConnectionId, cancellationToken))); }
            catch (ConnectionNotConfiguredException) { return Results.NotFound(); }
        });

        group.MapPost("/disconnect", async (ConnectionAction input, IGrainFactory grains, IEnumerable<IExternalConnection> external, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(input.ConnectionId)) { return Results.BadRequest(); }
            if (external.FirstOrDefault(account => account.ConnectionId == input.ConnectionId) is { } oauth)
            {
                await oauth.DisconnectAsync(grains, cancellationToken);
                return Results.Ok();
            }

            await Registry(grains).Disconnect(input.ConnectionId, cancellationToken);
            return Results.Ok();
        });
    }

    private static BrainScope CurrentScope()
    {
        var caller = CallerContextStamper.Require();
        return BrainScope.Create(caller.AccountId, caller.BrainId);
    }

    private static IConnectionRegistry Registry(IGrainFactory grains) => grains.GetGrain<IConnectionRegistry>(CurrentScope().Id);

    private static ConnectionRow Project(Connection account)
        => new(account.Id, account.IntegrationId, Status(account.Status), account.LastProbedAt, AuthMethodId: account.AuthMethodId);
}
