using DigitalBrain;
using DigitalBrain.Capabilities.Connections;

namespace IntoChat.OS.Modules.Identity.Contracts.Connections;

/// <summary>
/// Opens a vault secret reference without exposing plaintext to callers, binds it to a connection neuron
/// via <see cref="IConnection.Connect"/> or <see cref="IConnectionInbound.AcceptGrant"/>, and optionally probes it.
/// </summary>
public interface IConnectionSecretBinder
{
    Task<ConnectionView> BindAsync(
        BrainId brain,
        string connectorId,
        string connectionKey,
        string authMethodId,
        string secretRef,
        Func<string, string>? formatValue = null,
        bool probe = true,
        CancellationToken ct = default);

    Task<ConnectionView> BindGrantAsync(
        BrainId brain,
        string connectorId,
        string connectionKey,
        string authMethodId,
        string secretRef,
        string? target,
        string? scopes = null,
        Func<string, string>? formatValue = null,
        bool probe = true,
        CancellationToken ct = default);
}
