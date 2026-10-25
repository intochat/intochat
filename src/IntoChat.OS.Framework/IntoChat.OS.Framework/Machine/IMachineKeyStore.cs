namespace IntoChat.OS.Framework.Machine;

// The machine's ECDsa P-256 key (D-09, D-11). Sign-only: the private key never leaves the store; only signatures
// (IEEE P1363 r||s over SHA-256, the JWS ES256 form) and the SubjectPublicKeyInfo do.
public interface IMachineKeyStore
{
    // "tpm", "keychain" or "file".
    string Custody { get; }

    ValueTask EnsureKeyAsync(CancellationToken cancellationToken);

    ValueTask<byte[]> PublicKeySpkiAsync(CancellationToken cancellationToken);

    ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    ValueTask DeleteAsync(CancellationToken cancellationToken);
}
