using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Runtime.Orleans.Capabilities;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.Cloud.Auth.Services;

// Auth's door to secrets@1 (D-08): the TOTP seed is sealed bound to its principal, and the deployment secrets (hashing key,
// sign-in issuer key, Google credentials) are read by the names the manifest declares. Every call runs stamped as the auth
// module, so the runtime checks the manifest. Values are returned to the caller and never logged here.
internal sealed class AuthSecrets(IServiceProvider services)
{
    public const string HashingKey = "auth-hashing:HashingKey";
    public const string SigningKey = "signin-issuer:SigningKey";
    public const string GoogleClientId = "google-signin:ClientId";
    public const string GoogleClientSecret = "google-signin:ClientSecret";

    private ISecrets Secrets => services.GetRequiredKeyedService<ISecrets>(Capability.Secrets.V1Id);

    // The seed sealed for the principal; opening with another principal's context is refused.
    public async Task<SealedSecret> SealTotp(string principalId, byte[] seed)
    {
        using var stamp = Stamp();
        return await Secrets.Seal(TotpContext(principalId), seed);
    }

    public async Task<byte[]> OpenTotp(string principalId, SealedSecret sealedSeed)
    {
        using var stamp = Stamp();
        return await Secrets.Open(TotpContext(principalId), sealedSeed);
    }

    // The value of a deployment secret, or null when the deployment configured none.
    public async Task<string?> Named(string name)
    {
        using var stamp = Stamp();
        var reference = await Secrets.Named(name);
        return reference.Status == SecretStatus.Set ? await Secrets.Resolve(reference) : null;
    }

    internal static string TotpContext(string principalId) => "auth/totp/" + principalId;

    // The caller the runtime reads when the call reaches a capability: the auth module itself.
    internal static IDisposable Stamp()
        => RuntimeCallerContext.Stamp(new CapabilityCaller(AuthModule.Id, AuthModule.Id, "system", AuthModule.Id));
}
