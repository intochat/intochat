using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Secrets;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Framework.Modules;
using OsSecrets = IntoChat.OS.Connections.Platform.Secrets.ISecrets;
using OsSecretRef = IntoChat.OS.Connections.SecretRef;
using OsSealedSecret = IntoChat.OS.Connections.Platform.Connections.SealedSecret;
using SealedSecret = DigitalBrain.Capabilities.Secrets.SealedSecret;
using SecretRef = DigitalBrain.Capabilities.Secrets.SecretRef;
using SecretStatus = DigitalBrain.Capabilities.Secrets.SecretStatus;

namespace IntoChat.OS.Kernel.Composition.Capabilities;

// secrets@1's vault port over the existing Authority vault grain (05-12, D-15): the runtime's SecretsCapability reads the stamped
// caller and hands it here; Authority is not edited. A caller stores in and removes from its own principal's vault; it resolves
// only references its own vault issued (another owner's reference is denied, a removed one not found). Sealing uses the brain
// key sealer (IBrainSecretSealer) of the caller's brain with the context inside the sealed text, so another context never opens.
public sealed class AuthorityVaultPort(IntoChat.OS.Authority.IAuthorityGrains authority, IBrainSecretSealer? sealer = null) : ISecretVaultPort
{
    private const string RuntimeApp = "runtime";

    public async Task<SecretVaultResult> Set(CapabilityCaller caller, string name, string label, string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        try
        {
            var stored = await Vault(caller).Set(Owner(caller), name, label, value, cancellationToken).ConfigureAwait(false);
            return SecretVaultResult.Stored(ToRef(stored));
        }
        catch (InvalidOperationException) { return SecretVaultResult.Denied; }
    }

    public async Task<SecretVaultStatus> Remove(CapabilityCaller caller, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        try
        {
            await Vault(caller).Remove(Owner(caller), name, cancellationToken).ConfigureAwait(false);
            return SecretVaultStatus.Ok;
        }
        catch (InvalidOperationException) { return SecretVaultStatus.Denied; }
    }

    public async Task<SecretVaultResult> Resolve(CapabilityCaller caller, SecretRef secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.IsNamed) { return SecretVaultResult.Denied; }
        if (!SecretReferences.IsReference(secret.Reference)) { return SecretVaultResult.NotFound; }
        var reference = SecretReferences.FromReference(secret.Reference, secret.Label);
        string owner;
        try { owner = reference.OwnerOf(); }
        catch (InvalidOperationException) { return SecretVaultResult.NotFound; }
        if (!string.Equals(owner, caller.PrincipalId, StringComparison.Ordinal)) { return SecretVaultResult.Denied; }
        try
        {
            // The vault releases plaintext only to a trusted outbound call: the runtime, on behalf of the stamped caller's app.
            return SecretVaultResult.Resolved(await Vault(caller).Resolve(Platform(caller), reference, cancellationToken).ConfigureAwait(false));
        }
        catch (InvalidOperationException error) when (error.Message.Contains("another owner", StringComparison.Ordinal)) { return SecretVaultResult.Denied; }
        catch (InvalidOperationException) { return SecretVaultResult.NotFound; }
    }

    public async Task<SealedSecret?> Seal(CapabilityCaller caller, string context, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (sealer is null || string.IsNullOrWhiteSpace(context)) { return null; }
        var sealedText = await sealer.Seal(caller.BrainId, context, Bound(context, plaintext.Span), cancellationToken).ConfigureAwait(false);
        return new SealedSecret { Cipher = sealedText.Ciphertext, KeyId = sealedText.KeyId };
    }

    public async Task<byte[]?> Open(CapabilityCaller caller, string context, SealedSecret sealedSecret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(sealedSecret);
        if (sealer is null || string.IsNullOrWhiteSpace(context)) { return null; }
        string? bound = null;
        // A tampered cipher, another key id or a value sealed elsewhere does not open: the capability answers Denied.
        try { bound = await sealer.Open(caller.BrainId, new OsSealedSecret(context, sealedSecret.KeyId, sealedSecret.Cipher), cancellationToken).ConfigureAwait(false); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
        catch (FormatException) { return null; }
        catch (ArgumentException) { return null; }
        var prefix = Prefix(context);
        return bound.StartsWith(prefix, StringComparison.Ordinal) ? Convert.FromBase64String(bound[prefix.Length..]) : null;
    }

    private OsSecrets Vault(CapabilityCaller caller) => authority.Get<OsSecrets>(caller.PrincipalId);

    private static CallerContext Owner(CapabilityCaller caller) => new()
    {
        PrincipalId = caller.PrincipalId,
        AccountId = caller.AccountId,
        BrainId = caller.BrainId,
        Kind = CallerKind.User,
        StampedBy = TrustedEdge.AuthenticatedHttp,
        AppId = caller.AppId,
        ConversationId = caller.ConversationId,
    };

    private static CallerContext Platform(CapabilityCaller caller) => new()
    {
        PrincipalId = caller.PrincipalId,
        AccountId = caller.AccountId,
        BrainId = caller.BrainId,
        Kind = CallerKind.Platform,
        StampedBy = TrustedEdge.Platform,
        AppId = caller.AppId is { Length: > 0 } app ? app : RuntimeApp,
        ConversationId = caller.ConversationId,
    };

    private static SecretRef ToRef(OsSecretRef stored) => new()
    {
        Reference = stored.Reference,
        Label = stored.Label,
        Status = stored.IsSet ? SecretStatus.Set : SecretStatus.Unset,
    };

    // The sealer takes non-empty text: the context, a separator, then the bytes in base64 (empty bytes give an empty tail).
    private static string Prefix(string context) => context + "\n";

    private static string Bound(string context, ReadOnlySpan<byte> plaintext) => Prefix(context) + Convert.ToBase64String(plaintext);
}
