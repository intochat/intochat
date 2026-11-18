using DigitalBrain;
using DigitalBrain.UI;

namespace IntoChat.OS.Apps.Controls;

/// <summary>
/// The base of every control neuron. A control stores only what the user changes; its setup (label, enabled, content) is the props
/// its parent writes on the slot, and the parent refuses commands to a disabled slot, so a control has no enabled check of its own.
/// </summary>
/// <typeparam name="TState">What the user changes, stored by the control.</typeparam>
/// <typeparam name="TProps">What the parent decides, written on the slot (it names the props record the catalog and the client read).</typeparam>
public abstract class ControlNeuron<TState, TProps> : Screen<TState>, IControl<TProps> where TState : class, new() where TProps : ControlProps
{
    /// <summary>The key the control draws its view under: the last segment of its address (a slot's child key).</summary>
    protected string Key => ControlKeys.Of(Id);
}
