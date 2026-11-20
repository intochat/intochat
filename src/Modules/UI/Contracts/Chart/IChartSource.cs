using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.Chart;

/// <summary>A data neuron that supplies chart series in response to a <see cref="ChartQuery"/>.</summary>
[NeuronAlias("ui.chart-source")]
public interface IChartSource
{
    [ReadOnlyCall]
    Task<ChartSourceResult> Query(ChartQuery query);
}
