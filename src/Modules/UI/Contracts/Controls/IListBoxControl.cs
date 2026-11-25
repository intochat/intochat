using DigitalBrain.Contracts;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A list box: it owns the selected item. A screen mounts it with <c>Child&lt;IListBoxControl, ListBoxProps&gt;(key, props)</c>.</summary>
[NeuronAlias("control.listBox")]
[DefaultNeuronType("control.listBox")]
public interface IListBoxControl : IControl<ListBoxProps>;
