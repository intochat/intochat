using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A slider, whose label and range are its parent's props: it owns its value. A screen mounts it with <c>Child&lt;ISliderControl, SliderProps&gt;(key, props)</c>.</summary>
[NeuronAlias("control.slider")]
[DefaultNeuronType("control.slider")]
public interface ISliderControl : IScreen, IControl<SliderProps>;
