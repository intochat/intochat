using DigitalBrain.Identity.Local.Custody;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Framework.Parts;

// Holds each user's master key (Q3, Q6). Callers hand it secrets to wrap; the master key never leaves it.
// The platform's default derives it from a configured root; a per-OS module (Windows: the TPM) holds it in hardware.
public interface IMasterKeyCustody
{
    byte[] Wrap(string accountId, string context, byte[] secret);

    byte[] Unwrap(string accountId, string context, byte[] wrapped);
}
