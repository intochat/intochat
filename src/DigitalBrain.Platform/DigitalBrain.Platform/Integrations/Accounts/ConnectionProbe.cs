namespace DigitalBrain.Platform.Integrations.Accounts;

public enum ConnectionProbeOutcome
{
    Connected = 0,
    Expired = 1,
    Failing = 2,
}

[GenerateSerializer, Alias("connections.probe-result")]
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

[GenerateSerializer, Alias("connections.not-configured")]
public sealed class ConnectionNotConfiguredException(string message) : InvalidOperationException(message);
