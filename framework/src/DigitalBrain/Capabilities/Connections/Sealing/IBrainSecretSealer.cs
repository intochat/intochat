using DigitalBrain.Capabilities.Secrets;

namespace DigitalBrain.Capabilities.Connections.Sealing;

// Seals and opens connection credentials under a brain's core part key (R130: circle brain connection sharing).
// The owner's own connections stay under the master key via ISecretSealer; a shared copy's secrets are sealed here
// with KeyId "brain:{brainId}" so any machine holding that brain's core part opens them.
public interface IBrainSecretSealer
{
    Task<SealedSecret> Seal(string brainId, string name, string plaintext);
    Task<string> Open(string brainId, SealedSecret secret);
}

public static class BrainSeal
{
    /// <summary>Legacy v1 prefix (<c>brain:</c>); retained for read-only dual-open compatibility.</summary>
    public const string KeyPrefix = "brain:";

    /// <summary>Current v2 prefix (<c>brain2:</c>) emitted by <c>LocalSecretSealer</c> under <c>IMasterKeyCustody</c>.</summary>
    public const string KeyPrefixV2 = "brain2:";

    public static string KeyId(string brainId) => KeyPrefixV2 + brainId;

    public static bool IsBrainSealed(SealedSecret secret)
        => secret?.KeyId is { } keyId
            && (keyId.StartsWith(KeyPrefixV2, StringComparison.Ordinal)
                || keyId.StartsWith(KeyPrefix, StringComparison.Ordinal));
}
