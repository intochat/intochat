namespace DigitalBrain.Framework.Platform.Connections;

// Seals a selected connection credential for its destination brain without plaintext in grain calls.
public interface IBrainSecretSealer
{
    Task<SealedSecret> Seal(string brainId, string name, string plaintext, CancellationToken cancellationToken = default);

    Task<string> Open(string brainId, SealedSecret secret, CancellationToken cancellationToken = default);
}

public static class BrainSeal
{
    // The KeyId prefix of a secret wrapped under the destination brain's key.
    public const string Prefix = "pk1:";

    public static bool IsBrainSealed(SealedSecret secret) => secret.KeyId.StartsWith(Prefix, StringComparison.Ordinal);
}
