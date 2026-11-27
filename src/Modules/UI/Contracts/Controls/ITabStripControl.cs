using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A tab strip: it owns the selected tab. A screen mounts it with <c>Child&lt;ITabStripControl, TabStripProps&gt;(key, props)</c>.</summary>
[NeuronAlias("control.tabStrip")]
[DefaultNeuronType("control.tabStrip")]
public interface ITabStripControl : IScreen, IControl<TabStripProps>;
