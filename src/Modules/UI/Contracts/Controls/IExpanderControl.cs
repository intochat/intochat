using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>An expander: it owns whether its content is shown. A screen mounts it with <c>Child&lt;IExpanderControl, ExpanderProps&gt;(key, props)</c>.</summary>
[NeuronAlias("control.expander")]
[DefaultNeuronType("control.expander")]
public interface IExpanderControl : IScreen, IControl<ExpanderProps>;
