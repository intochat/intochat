using DigitalBrain.Capabilities;
using System.Text.Json.Serialization;
using DigitalBrain.Access;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Connections.Platform.Connections;

// The shell reads the status by name.
[JsonConverter(typeof(JsonStringEnumConverter<ConnectionStatus>))]
public enum ConnectionStatus
{
    Connected = 0,
    Expired = 1,
    Failing = 2,
    Disconnected = 3,
}

// The connector's edge neuron is the connection: keyed {brain}/connection/{connector}/{id}, it owns
// its credential as sealed state. The shell, the assistant and module tools talk to this face.
// A plain contract (06-08) that every connector's neuron implements, so it names no neuron type: the connection key's connector
// does (ConnectionGrains resolves it, R4-21(2)).
[DigitalBrain.Contracts.NeuronAlias("connection"), IntoChat.OS.Runtime.ConnectionContract]
public interface IConnection
{
    [DigitalBrain.ReadOnlyCall] Task<ConnectionView> Read();
    Task<ConnectionView> Connect(string authMethodId, string value);
    Task<ConnectionView> Probe();
    Task Disconnect();

    // R147: a circle brain's copy of a shared connection: its definition, no credential, not connected (Task 28 seals one).
    [Internal] Task<ConnectionView> Adopt(string authMethodId, string? account);

    // R130: re-seals this connection's credential under the target brain's core part key, so a machine holding that
    // part opens it. No plaintext crosses this call: the returned secrets are ciphertext only.
    [Internal] Task<SealedSecret[]> ExportFor(string brainId);

    // R130/R147: fills a shared copy's credential from secrets already sealed under this brain's core part key.
    [Internal] Task<ConnectionView> Import(string authMethodId, string? account, SealedSecret[] secrets, ConnectionStatus status, string? detail);

    // R146: the owner's connection records an app of another brain it is shared into; that record is what the call
    // gate admits the app's use operations by (IConnectionBindings).
    // Phase F: a connection is shared with principals. ShareInto(brain, app) is ShareWith(AppPrincipal.Id(app)) for
    // an app of that brain, kept as an alias.
    [Internal] Task ShareInto(string brainId, string app);

    [Internal, DigitalBrain.Interleaves] Task<bool> IsSharedInto(string brainId, string app);

    // R155: the sharing is withdrawn (its sharer left the brain it was shared into).
    [Internal] Task UnshareFrom(string brainId, string app);

    [Internal] Task ShareWith(string principalId);

    [Internal, DigitalBrain.Interleaves] Task<bool> IsSharedWith(string principalId);

    [Internal] Task UnshareWith(string principalId);
}

// Reached only by the Platform OAuth callback, which stamps the owner recorded at login start. A plain contract (06-08) of every
// connection neuron, addressed by the connection key's connector like IConnection.
[DigitalBrain.Contracts.NeuronAlias("connection.inbound"), IntoChat.OS.Runtime.ConnectionContract]
public interface IConnectionInbound
{
    Task<ConnectionView> AcceptGrant(OAuthGrant grant);
}

[GenerateSerializer, Alias("connection.view")]
public sealed record ConnectionView(
    [property: Id(0)] string ConnectorId,
    [property: Id(1)] string ConnectionId,
    [property: Id(2)] string AuthMethodId,
    [property: Id(3)] ConnectionStatus Status,
    [property: Id(4)] string? Account,
    [property: Id(5)] DateTimeOffset? ExpiresAt,
    [property: Id(6)] DateTimeOffset? LastProbedAt,
    [property: Id(7)] string? Detail);

// The token response, parsed by the Platform. Target is what the user asked to connect (a repository
// URL, an instance); Response is the raw token JSON for the module's own parsing.
[GenerateSerializer, Alias("connection.oauth-grant")]
public sealed record OAuthGrant(
    [property: Id(0)] string AuthMethodId,
    [property: Id(1)] string AccessToken,
    [property: Id(2)] string? RefreshToken,
    [property: Id(3)] DateTimeOffset? ExpiresAt,
    [property: Id(4)] string Scopes,
    [property: Id(5)] string? Target,
    [property: Id(6)] string Response)
{
    private bool PrintMembers(System.Text.StringBuilder builder) => builder.Append("<redacted>") is not null;
}

// Opened secret, alive for one provider call inside the grain turn.
public sealed record Credential(string Value, string? Refresh)
{
    private bool PrintMembers(System.Text.StringBuilder builder) => builder.Append("<redacted>") is not null;
}

public enum ConnectionProbeOutcome { Connected = 0, Expired = 1, Failing = 2 }

[GenerateSerializer, Alias("connection.probe-result")]
public sealed record ConnectionProbeResult([property: Id(0)] ConnectionProbeOutcome Outcome, [property: Id(1)] string Detail)
{
    public static ConnectionProbeResult Connected(string detail = "The connector probe succeeded.") => new(ConnectionProbeOutcome.Connected, detail);
    public static ConnectionProbeResult Expired(string detail) => new(ConnectionProbeOutcome.Expired, detail);
    public static ConnectionProbeResult Failing(string detail) => new(ConnectionProbeOutcome.Failing, detail);
}
