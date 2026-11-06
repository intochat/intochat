using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

[NeuronAlias("ino.profile-writer"), DefaultNeuronType("ino.profile-writer")]
public interface IProfileContextWriter
{
    // Starts following the user's settings and writes the user.profile context from them now. Idempotent.
    Task Ensure();
}
