namespace DigitalBrain.Framework.Platform.Connections;

// R130: a connection shared into another brain is sealed under that brain's core part key, not the sharer's master
// key, so any machine holding the part opens it. The owner's machine re-seals the credential here without the
// plaintext crossing a call. A host with no part keys (the trust side, a plain test host) registers none.
public interface IPartSecretSealer
{
    Task<SealedSecret> Seal(string brainId, string name, string plaintext, CancellationToken cancellationToken = default);

    Task<string> Open(string brainId, SealedSecret secret, CancellationToken cancellationToken = default);
}

public static class PartSeal
{
    // The KeyId prefix of a secret wrapped under a brain's core part key rather than the machine's master key.
    public const string Prefix = "pk1:";

    public static bool IsPartSealed(SealedSecret secret) => secret.KeyId.StartsWith(Prefix, StringComparison.Ordinal);
}
