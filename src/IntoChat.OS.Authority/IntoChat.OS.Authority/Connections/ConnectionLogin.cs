using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Connections;
using IntoChat.OS.Framework.Platform.Integrations;
using IntoChat.OS.Authority.Connections.OAuth;
using IntoChat.OS.Authority.Integrations;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Connections;

// What a login start did: the provider's page, or why not (Conflict: 409, else 400).
internal sealed record LoginStart(Uri? Url, bool Conflict, string? Error);

// The one browser-login start, behind the user's own HTTP session (the shell's connection window, also
// from the desktop gallery's Connect). No app starts a login: connections never belong to an app.
internal static class ConnectionLogin
{
    public static async Task<LoginStart> StartAsync(IServiceProvider services, string brainId, string connectionId, string connectorId, string authMethodId, string? target, CancellationToken ct)
    {
        var neurons = services.GetRequiredService<AuthorityNeurons>();
        if (await UnavailableError(services, connectorId, ct) is { } unavailable) { return new(null, true, unavailable); }
        if (await TakenError(neurons, connectionId, connectorId) is { } taken) { return new(null, true, taken); }
        var key = ConnectionKeys.For(brainId, connectorId, connectionId);
        try
        {
            // The callback grants into this key's neuron; refuse now rather than after the provider round trip.
            _ = neurons.CallerConnection<IConnection>(services, key);
            return new(await services.GetRequiredService<OAuthLoginFlow>().StartAsync(key, authMethodId, target, CallerContextStamper.Require(), ct), false, null);
        }
        catch (ArgumentException error) { return new(null, false, error.Message); }
        catch (InvalidOperationException error) { return new(null, true, error.Message); }
    }

    // Probe and disconnect name a connection by id alone, so connect and login refuse an id the brain
    // already tracks under another connector; the index then holds at most one key per id.
    public static async Task<string?> TakenError(AuthorityNeurons neurons, string connectionId, string connectorId)
        => await ConnectionEndpoints.IsTakenByAnotherConnector(neurons, connectionId, connectorId)
            ? $"Connection '{connectionId}' already belongs to another connector in this brain." : null;

    // A declared connector whose required Integrations the deployment has not made Ready is hidden by
    // the catalog; it takes no new connection either. Names the connector only, never a field value.
    public static async Task<string?> UnavailableError(IServiceProvider services, string connectorId, CancellationToken ct)
        => services.GetRequiredService<IReadOnlyList<ConnectorDefinition>>().Any(connector => connector.Id == connectorId)
            && !(await services.GetRequiredService<ConnectorCatalog>().ListAsync(ct)).Any(entry => entry.Id == connectorId)
            ? $"Connector '{connectorId}' is not offered by this deployment." : null;
}
