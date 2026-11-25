using DigitalBrain.Contracts;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A check box: it owns whether it is checked. A screen mounts it with <c>Child&lt;ICheckBoxControl, CheckBoxProps&gt;(key, props)</c>.</summary>
[NeuronAlias("control.checkBox")]
[DefaultNeuronType("control.checkBox")]
public interface ICheckBoxControl : IControl<CheckBoxProps>;
