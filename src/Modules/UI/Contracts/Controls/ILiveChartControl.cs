using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A live chart: it owns the points a feed adds. A screen mounts it with <c>Child&lt;ILiveChartControl, LiveChartProps&gt;(key, props)</c>.</summary>
[NeuronAlias("control.liveChart")]
[DefaultNeuronType("control.liveChart")]
public interface ILiveChartControl : IScreen, IControl<LiveChartProps>;
