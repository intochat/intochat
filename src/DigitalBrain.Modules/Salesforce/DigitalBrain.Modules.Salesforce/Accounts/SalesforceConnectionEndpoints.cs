using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace DigitalBrain.Salesforce;

// The neutral accounts surface is Framework's IExternalConnection; Salesforce contributes its OAuth
// grant as an external account and the browser-login discovery and start routes.
internal sealed class SalesforceExternalConnection : IExternalConnection
{
    public const string OAuthConnectionId = "oauth:salesforce";

    public string ConnectionId => OAuthConnectionId;

    public async Task<ConnectionRow?> ReadAsync(IGrainFactory grains, CancellationToken cancellationToken)
    {
        var record = await grains.GetGrain<ISalesforce>(BrainScope.CurrentId()).ReadConnection();
        return record.Connected ? Project(record) : null;
    }

    public async Task DisconnectAsync(IGrainFactory grains, CancellationToken cancellationToken)
        => await grains.GetGrain<ISalesforce>(BrainScope.CurrentId()).Disconnect(new());

    private static ConnectionRow Project(SalesforceConnection record) => new(
        OAuthConnectionId, "salesforce",
        record.ExpiresAt <= DateTimeOffset.UtcNow ? "Expired" : "Configured",
        Account: record.InstanceUrl, AuthMethodId: "org-oauth");
}

internal static class SalesforceConnectionEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = BrainRoutes.Group(endpoints, "/connections");
        group.MapGet("/services", async (IEnumerable<BrowserLogins> logins, [FromServices] SalesforceOAuthConfiguration settings, CancellationToken ct) =>
        {
            var available = await settings.IsConfiguredAsync(ct);
            return Results.Ok(logins.Select(login => new
            {
                id = login.Definition.Provider,
                name = login.Definition.DisplayName,
                available = available && login.Definition.Provider == "salesforce",
                reason = available && login.Definition.Provider == "salesforce" ? null : "Operator OAuth setup is required.",
                capabilities = "Provider authorization",
            }));
        });
        group.MapPost("/services/{provider}/start", async (string provider, IEnumerable<BrowserLogins> logins, [FromServices] SalesforceOAuthConfiguration settings, CancellationToken ct) =>
        {
            var login = logins.FirstOrDefault(value => value.Definition.Provider == provider);
            if (login is null) { return Results.NotFound(); }
            if (provider != "salesforce") { return Results.Json(new { error = "This provider requires a service-specific setup flow." }, statusCode: 503); }
            if (!await settings.IsConfiguredAsync(ct)) { return Results.Json(new { error = "Provider authorization is unavailable. Check the deployment configuration." }, statusCode: 503); }
            try { return Results.Ok(new { url = login.Require(new BrowserLoginWorkspace(BrainScope.CurrentId()).ToScope()).AbsoluteUri }); }
            catch (InvalidOperationException) { return Results.Json(new { error = "Provider authorization is unavailable. Check the deployment configuration." }, statusCode: 503); }
        });
    }
}
