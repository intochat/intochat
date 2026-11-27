using System.Security.Cryptography;

namespace IntoChat.OS.Modules.Devices.Link;

// Exposes non-secret relay server discovery and header-only request/WebSocket signing backed by link.bin.
// Raw PKCS#8 private keys and refresh tokens stay encapsulated inside DeviceCredentials/CloudClient.
internal sealed class DeviceLinkCredentialsProvider(
    DeviceCredentials credentials,
    CloudClient cloud,
    TimeProvider clock) : IDeviceLinkCredentialsProvider
{
    public Uri? TryGetServerUri() => credentials.TryReadServerUri();

    public async ValueTask<bool> TryAuthorizeRequestAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri is null || !HasMatchingOrigin(request.RequestUri, credentials.TryReadServerUri()))
        {
            return false;
        }

        var secrets = await cloud.TryGetFreshSecretsAsync(forceRefresh: false, ct).ConfigureAwait(false);
        if (secrets is null || !HasMatchingOrigin(request.RequestUri, secrets.Server))
        {
            return false;
        }

        CloudRelayRequestSigner.SignWithMachineProof(request, secrets.AccessToken, secrets.PrivateKeyPkcs8, clock);
        return true;
    }

    public async ValueTask<DeviceStreamCredentials?> TryCreateStreamCredentialsAsync(
        Uri httpStreamUri,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(httpStreamUri);
        if (!HasMatchingOrigin(httpStreamUri, credentials.TryReadServerUri()))
        {
            return null;
        }

        var secrets = await cloud.TryGetFreshSecretsAsync(forceRefresh, ct).ConfigureAwait(false);
        if (secrets is null || !HasMatchingOrigin(httpStreamUri, secrets.Server))
        {
            return null;
        }

        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(secrets.PrivateKeyPkcs8, out _);
        var proof = MachineProof.Sign(key, "GET", httpStreamUri, secrets.AccessToken, clock.GetUtcNow());
        return new DeviceStreamCredentials(secrets.AccessToken, proof, secrets.AccessExpiresAt);
    }

    internal static bool HasMatchingOrigin(Uri? targetUri, Uri? serverUri)
    {
        if (targetUri is null || serverUri is null || !targetUri.IsAbsoluteUri || !serverUri.IsAbsoluteUri)
        {
            return false;
        }

        return targetUri.Port > 0
            && string.Equals(targetUri.Scheme, serverUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(targetUri.IdnHost, serverUri.IdnHost, StringComparison.OrdinalIgnoreCase)
            && targetUri.Port == serverUri.Port;
    }
}
