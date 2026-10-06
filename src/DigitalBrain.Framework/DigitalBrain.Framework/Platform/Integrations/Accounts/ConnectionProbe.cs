// Orleans aliases retain the original wire identities across the SDK namespace rename.
namespace DigitalBrain.Framework.Platform.Integrations.Accounts;

[Alias("DigitalBrain.Framework.Platform.Integrations.Accounts.AccountProbeOutcome")]
public enum ConnectionProbeOutcome
{
    Connected = 0,
    Expired = 1,
    Failing = 2,
}

[GenerateSerializer, Alias("connections.probe-result")] // alias predates the integrations rename; persisted, do not touch
public sealed record ConnectionProbeResult(ConnectionProbeOutcome Outcome, string Detail)
{
    public static ConnectionProbeResult Connected(string detail = "The connector probe succeeded.") => new(ConnectionProbeOutcome.Connected, detail);

    public static ConnectionProbeResult Expired(string detail) => new(ConnectionProbeOutcome.Expired, detail);

    public static ConnectionProbeResult Failing(string detail) => new(ConnectionProbeOutcome.Failing, detail);
}

// Integrations can replace the default presence check with a source-specific probe.
public interface IConnectionProbe
{
    Task<ConnectionProbeResult> ProbeAsync(string source, string credential, CancellationToken cancellationToken = default);
}

[GenerateSerializer, Alias("connections.not-configured")] // alias predates the integrations rename; persisted, do not touch
public sealed class ConnectionNotConfiguredException(string message) : InvalidOperationException(message);
