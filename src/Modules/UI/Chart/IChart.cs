using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.Chart;

/// <summary>
/// A stateful, addressable chart neuron supporting both streaming points (200-point rolling window
/// with EventId deduplication) and live query binding against an <see cref="IChartSource"/> neuron.
/// </summary>
[NeuronAlias("ui.chart")]
[DefaultNeuronType("ui.chart")]
public interface IChart : IUiInteractiveControl
{
    /// <summary>Replaces the chart's full static or multi-series data snapshot.</summary>
    Task<ChartState> Render(ChartSpec spec);

    /// <summary>Appends a streaming point (capped at 200 points per series; duplicate EventId ignored).</summary>
    Task<ChartState> Append(ChartPoint point, string? seriesName = null);

    /// <summary>Switches visualization kind (columns, line, area, pie) without losing data or query.</summary>
    Task<ChartState> SetKind(string kind);

    /// <summary>Selects or clears a data point for drill-down or cross-filtering.</summary>
    Task<ChartState> SelectPoint(string? label, string? seriesName = null);

    /// <summary>Updates chart documentation / notes (editable by user or agent).</summary>
    Task<ChartState> SetDocs(string description);

    /// <summary>Binds this chart to an <see cref="IChartSource"/> neuron and query, then loads data.</summary>
    Task<ChartState> Bind(string sourceNeuronId, ChartQuery query);

    /// <summary>Re-executes the bound query (if any) and updates points, status, and error.</summary>
    Task<ChartState> Refresh();

    [ReadOnlyCall]
    Task<ChartState> Read();
}
