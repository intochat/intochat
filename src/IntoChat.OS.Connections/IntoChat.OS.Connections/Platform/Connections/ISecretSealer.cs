using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Connections.Platform.Connections;

// Seals credential bytes for grain state. Every sealed secret carries its own master-wrapped data
// key, so storage holds ciphertext only and rotation is "re-seal on the next successful call".
public interface ISecretSealer
{
    SealedSecret Seal(string name, string plaintext);
    string Open(SealedSecret secret);
}
