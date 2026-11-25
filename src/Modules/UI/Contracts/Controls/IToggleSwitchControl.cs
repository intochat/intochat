using DigitalBrain.Contracts;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A toggle switch: it owns whether it is on. A screen mounts it with <c>Child&lt;IToggleSwitchControl, ToggleSwitchProps&gt;(key, props)</c>.</summary>
[NeuronAlias("control.toggleSwitch")]
[DefaultNeuronType("control.toggleSwitch")]
public interface IToggleSwitchControl : IControl<ToggleSwitchProps>;
