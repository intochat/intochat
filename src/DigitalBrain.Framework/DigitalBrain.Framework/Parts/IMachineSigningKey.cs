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
