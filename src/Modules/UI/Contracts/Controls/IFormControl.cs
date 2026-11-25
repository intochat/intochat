using DigitalBrain.Contracts;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A form: it keeps the last submitted values. A screen mounts it with <c>Child&lt;IFormControl, FormProps&gt;(key, props)</c>.</summary>
[NeuronAlias("control.form")]
[DefaultNeuronType("control.form")]
public interface IFormControl : IControl<FormProps>;
