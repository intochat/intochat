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
        var secrets = await cloud.TryGetFreshSecretsAsync(forceRefresh: false, ct).ConfigureAwait(false);
        if (secrets is null)
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
        var secrets = await cloud.TryGetFreshSecretsAsync(forceRefresh, ct).ConfigureAwait(false);
        if (secrets is null)
        {
            return null;
        }

        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(secrets.PrivateKeyPkcs8, out _);
        var proof = MachineProof.Sign(key, "GET", httpStreamUri, secrets.AccessToken, clock.GetUtcNow());
        return new DeviceStreamCredentials(secrets.AccessToken, proof, secrets.AccessExpiresAt);
    }
}
