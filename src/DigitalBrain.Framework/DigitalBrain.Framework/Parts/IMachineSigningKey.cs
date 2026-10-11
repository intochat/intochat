namespace DigitalBrain.Framework.Parts;

// This machine's P-256 signing key (R35): it signs head records and machine calls. The platform's default keeps it
// in a file; a per-OS module (Windows: the TPM) keeps it non-exportable in hardware.
public interface IMachineSigningKey
{
    // SubjectPublicKeyInfo of the P-256 key.
    byte[] PublicKey { get; }

    // ECDSA over SHA-256, IEEE P1363 format.
    byte[] Sign(byte[] data);
}

// R127: this machine's P-256 key-agreement key, registered beside its signing key. A circle part key is wrapped to
// it by ephemeral-static ECDH; the software default keeps it in a file, the Windows module in the TPM where the
// provider does ECDH.
public interface IMachineAgreementKey
{
    // SubjectPublicKeyInfo of the P-256 key.
    byte[] PublicKey { get; }

    // SHA-256 of the ECDH shared secret with the peer's SubjectPublicKeyInfo (what CNG and OpenSSL both derive).
    byte[] DeriveShared(byte[] ephemeralPublicKey);
}
