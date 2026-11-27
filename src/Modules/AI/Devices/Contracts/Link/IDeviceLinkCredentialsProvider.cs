namespace IntoChat.OS.Modules.Devices.Link;

/// <summary>
/// Non-secret credentials snapshot and header-signing contract for a desktop machine linked via <c>link.bin</c>.
/// Raw P-256 private keys and refresh tokens never cross this boundary.
/// </summary>
public interface IDeviceLinkCredentialsProvider
{
    /// <summary>
    /// Returns the linked cloud relay base URI from <c>link.bin</c> when present and not revoked,
    /// or <see langword="null"/> when the desktop is not linked. Never returns tokens or keys.
    /// </summary>
    Uri? TryGetServerUri();

    /// <summary>
    /// Authorizes <paramref name="request"/> with a fresh <c>Authorization: Bearer &lt;token&gt;</c>
    /// and <c>X-Machine-Proof</c> header derived from <c>link.bin</c>, refreshing expiring tokens automatically.
    /// Returns <see langword="false"/> if no active device link exists.
    /// </summary>
    ValueTask<bool> TryAuthorizeRequestAsync(HttpRequestMessage request, CancellationToken ct = default);

    /// <summary>
    /// Produces a fresh <c>(AccessToken, MachineProof, ExpiresAt)</c> tuple bound to <c>GET</c> on
    /// <paramref name="httpStreamUri"/> for WebSocket upgrade headers or in-band <c>auth-refresh</c> frames.
    /// Returns <see langword="null"/> if no active device link exists.
    /// </summary>
    ValueTask<DeviceStreamCredentials?> TryCreateStreamCredentialsAsync(
        Uri httpStreamUri,
        bool forceRefresh = false,
        CancellationToken ct = default);
}

/// <summary>Short-lived access token and request-bound machine proof for a brain relay WebSocket stream.</summary>
public sealed record DeviceStreamCredentials(
    string AccessToken,
    string MachineProof,
    DateTimeOffset ExpiresAt);
