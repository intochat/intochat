using DigitalBrain.Neurons;

namespace IntoChat.OS.Modules.UI.Legacy;

[Neuron("ui.chat")]
internal sealed class LegacyChatThreadNeuron : Neuron<UiChatThreadState>, ILegacyChatThread
{
    public Task<UiChatThreadState> Read() => Task.FromResult(State);
}
