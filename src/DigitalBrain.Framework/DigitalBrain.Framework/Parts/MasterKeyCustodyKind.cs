namespace DigitalBrain.Framework.Parts;

// Where a machine's custody root lives (R35): a configured root (development; refused in production) or the TPM.
// Configured at DigitalBrainNames.CustodyConfigurationKey; a per-OS module reads it to register its hardware custody.
public enum MasterKeyCustodyKind
{
    Configured = 0,
    Tpm = 1,
}
