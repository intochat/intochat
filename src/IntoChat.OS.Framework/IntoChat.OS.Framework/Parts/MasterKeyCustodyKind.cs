namespace IntoChat.OS.Framework.Parts;

// Where a machine's custody root lives (R35): a configured root (development; refused in production) or the TPM.
// Configured at IntoChatOSNames.CustodyConfigurationKey; a per-OS module reads it to register its hardware custody.
// R79: switching kinds replaces the machine's signing key, so its machine id changes (it re-registers as a new machine,
// R52) and part keys wrapped under the previous custody no longer unwrap (the R74 error).
public enum MasterKeyCustodyKind
{
    Configured = 0,
    Tpm = 1,
}
