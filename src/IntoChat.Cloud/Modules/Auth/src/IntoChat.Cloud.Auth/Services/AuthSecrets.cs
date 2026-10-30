using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.Cloud.Auth.Services;

// Auth's door to secrets@1 (D-08): the TOTP seed is sealed bound to its principal, and the deployment secrets (hashing key,
// sign-in issuer key, Google credentials) are read by the names the manifest declares. Every call runs stamped as the auth
// module, so the runtime checks the manifest. Values are returned to the caller and never logged here.
internal sealed class AuthSecrets(IServiceProvider services, IAuthHostSeam host)
{
    // The names the manifest declares; the deployment carries each as IntoChatOS:Modules:auth:Secrets:{name}.
    public const string HashingKey = "auth-hashing";
    public const string SigningKey = "signin-issuer";
    public const string GoogleClientId = "google-client-id";
    public const string GoogleClientSecret = "google-client-secret";

    private ISecrets Secrets => services.GetRequiredKeyedService<ISecrets>(Capability.Secrets.V1Id);

    // The seed sealed for the principal; opening with another principal's context is refused.
    public async Task<SealedSecret> SealTotp(string principalId, byte[] seed)
    {
        using var stamp = Stamp(host);
        return await Secrets.Seal(TotpContext(principalId), seed);
    }

    public async Task<byte[]> OpenTotp(string principalId, SealedSecret sealedSeed)
    {
        using var stamp = Stamp(host);
        return await Secrets.Open(TotpContext(principalId), sealedSeed);
    }

    // The value of a deployment secret, or null when the deployment configured none.
    public async Task<string?> Named(string name)
    {
        using var stamp = Stamp(host);
        var reference = await Secrets.Named(name);
        if (reference.Status == SecretStatus.Set)
        {
            return await Secrets.Resolve(reference);
        }

        var configuration = services.GetService<IConfiguration>();
        var fallback = name switch
        {
            GoogleClientId => configuration?["IntoChatOS:Integrations:google-oauth:ClientId"],
            GoogleClientSecret => configuration?["IntoChatOS:Integrations:google-oauth:ClientSecret"],
            _ => null,
        };
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    internal static string TotpContext(string principalId) => "auth/totp/" + principalId;

    // The caller the runtime reads when the call reaches a capability: the auth module itself.
    internal static IDisposable Stamp(IAuthHostSeam host) => host.StampModuleCaller(Module);

    internal static CapabilityCaller Module { get; } = new(AuthModule.Id, AuthModule.Id, "system", AuthModule.Id);
}
