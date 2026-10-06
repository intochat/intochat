using DigitalBrain.Framework.Platform.Integrations.Accounts;
namespace DigitalBrain.Platform.Integrations.Accounts;

internal sealed class CredentialPresenceProbe : IConnectionProbe
{
    public Task<ConnectionProbeResult> ProbeAsync(string source, string credential, CancellationToken cancellationToken = default)
        => Task.FromResult(string.IsNullOrWhiteSpace(credential)
            ? ConnectionProbeResult.Failing("No credential is stored for this connector.")
            : ConnectionProbeResult.Connected("A credential is stored; the source has not been verified."));
}
