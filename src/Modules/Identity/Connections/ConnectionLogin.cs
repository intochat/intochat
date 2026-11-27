using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Runtime.Http.Connections.OAuth;
using DigitalBrain.Runtime.Orleans.Capabilities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Identity.Integrations;

namespace IntoChat.OS.Modules.Identity.Connections;

// What a login start did: the provider's page, or why not (Conflict: 409, else 400).
internal sealed record LoginStart(Uri? Url, bool Conflict, string? Error);

// The one browser-login start, behind the user's own HTTP session (the shell's connection window, also
// from the desktop gallery's Connect). No app starts a login: connections never belong to an app.
internal static class ConnectionLogin
{
    public static Task<LoginStart> StartAsync(IServiceProvider services, string brainId, string connectionId, string connectorId, string authMethodId, string? target, CancellationToken ct)
        => StartAsync(services, brainId, connectionId, connectorId, authMethodId, target, request: null, ct);

    public static async Task<LoginStart> StartAsync(IServiceProvider services, string brainId, string connectionId, string connectorId, string authMethodId, string? target, HttpRequest? request, CancellationToken ct)
    {
        var neurons = services.GetRequiredService<AuthorityNeurons>();
        if (await UnavailableError(services, connectorId, authMethodId, ct) is { } unavailable) { return new(null, true, unavailable); }
        var key = ConnectionKeys.For(brainId, connectorId, connectionId);
        try
        {
            // The callback grants into this key's neuron; refuse now rather than after the provider round trip.
            _ = neurons.CallerConnection<IConnection>(services, key);
            return new(await services.GetRequiredService<OAuthLoginFlow>().StartAsync(key, authMethodId, target, request, ct), false, null);
        }
        catch (ArgumentException error) { return new(null, false, error.Message); }
        catch (InvalidOperationException error) { return new(null, true, error.Message); }
    }

    // Multiple connection neurons per user/brain are supported via 3-part ConnectionKeys ({brain}/connection/{connector}/{id}).
    public static Task<string?> TakenError(AuthorityNeurons neurons, string connectionId, string connectorId)
    {
        _ = neurons;
        _ = connectionId;
        _ = connectorId;
        return Task.FromResult<string?>(null);
    }

    public static Task<string?> UnavailableError(IServiceProvider services, string connectorId, CancellationToken ct)
        => UnavailableError(services, connectorId, authMethodId: null, ct);

    // Validates availability per auth method: credential methods (pat, bearer-token) and public device flows
    // remain available even when an optional/confidential web OAuth integration is unconfigured.
    // Names the connector/method only, never a field value.
    public static async Task<string?> UnavailableError(IServiceProvider services, string connectorId, string? authMethodId, CancellationToken ct)
    {
        var connector = services.GetRequiredService<IReadOnlyList<ConnectorDefinition>>()
            .FirstOrDefault(c => string.Equals(c.Id, connectorId, StringComparison.Ordinal));
        if (connector is null)
        {
            return null;
        }

        var catalog = await services.GetRequiredService<ConnectorCatalog>().ListAsync(ct);
        var entry = catalog.FirstOrDefault(e => string.Equals(e.Id, connectorId, StringComparison.Ordinal));
        if (entry is null)
        {
            return $"Connector '{connectorId}' is not offered by this deployment.";
        }

        if (!string.IsNullOrWhiteSpace(authMethodId)
            && connector.AuthMethods.Contains(authMethodId, StringComparer.Ordinal)
            && !entry.AvailableAuthMethods.Contains(authMethodId, StringComparer.Ordinal))
        {
            return $"Authentication method '{authMethodId}' for connector '{connectorId}' is not configured by this deployment.";
        }

        return null;
    }
}
