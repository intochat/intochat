using DigitalBrain.Contracts;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A combo box: it owns the selected option. A screen mounts it with <c>Child&lt;IComboBoxControl, ComboBoxProps&gt;(key, props)</c>.</summary>
[NeuronAlias("control.comboBox")]
[DefaultNeuronType("control.comboBox")]
public interface IComboBoxControl : IControl<ComboBoxProps>;
