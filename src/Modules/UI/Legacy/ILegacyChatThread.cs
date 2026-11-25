using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.Legacy;

// The old ui.chat neuron, read-only: Engineer imports the brainstorm history an earlier version kept here once (owner decision 11).
// Nothing writes it. OWED.md names it for deletion two releases after the release that carries P-Engineer.
[NeuronAlias("ui.chat")]
[DefaultNeuronType("ui.chat")]
public interface ILegacyChatThread
{
    [ReadOnlyCall]
    Task<UiChatThreadState> Read();
}
