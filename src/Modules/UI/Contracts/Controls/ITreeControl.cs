using DigitalBrain.Contracts;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A tree, whose nodes are its parent's props: it owns the selected node and the expanded folders. A screen mounts it with <c>Child&lt;ITreeControl, TreeProps&gt;(key, props)</c>.</summary>
[NeuronAlias("control.tree")]
[DefaultNeuronType("control.tree")]
public interface ITreeControl : IControl<TreeProps>;
