using DigitalBrain.Neurons;

namespace IntoChat.OS.Modules.UI.Core;

/// <summary>The base class of every stateful UI neuron in <c>IntoChat.OS.Modules.UI</c>. Hover, press and focus stay in the client.</summary>
public abstract class UiNeuronBase<TState> : Neuron<TState>, IUiInteractiveControl
    where TState : class, new()
{
}
