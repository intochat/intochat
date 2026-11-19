using DigitalBrain.Neurons;

namespace IntoChat.OS.Modules.UI.Chart;

/// <summary>Built-in sample <see cref="IChartSource"/> for the UI Gallery and tests.</summary>
[Neuron("ui.sample-chart-source")]
internal sealed class SampleChartSource : Neuron, IChartSource
{
    private static readonly (string Quarter, string Label, double Current, double Baseline)[] Dataset =
    [
        ("q1", "Jan", 68, 54),
        ("q1", "Feb", 82, 62),
        ("q1", "Mar", 94, 71),
        ("q2", "Apr", 128, 88),
        ("q2", "May", 141, 96),
        ("q2", "Jun", 156, 108),
    ];

    public Task<ChartSourceResult> Query(ChartQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var filter = query.Filter?.Trim() ?? "";
        var rows = string.IsNullOrWhiteSpace(filter) || string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase)
            ? Dataset
            : Dataset.Where(r => r.Quarter.Equals(filter, StringComparison.OrdinalIgnoreCase)
                || r.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (query.Limit is > 0 and var limit)
        {
            rows = [.. rows.Take(limit)];
        }

        IReadOnlyList<ChartSeries> series =
        [
            new("2026 (Active)", [.. rows.Select(r => new ChartPoint(r.Label, r.Current))]),
            new("2025 (Baseline)", [.. rows.Select(r => new ChartPoint(r.Label, r.Baseline))]),
        ];

        var title = string.IsNullOrWhiteSpace(filter) || string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase)
            ? "Quarterly Throughput (2026 vs 2025)"
            : $"Quarterly Throughput · Filter: {filter.ToUpperInvariant()}";

        return Task.FromResult(new ChartSourceResult(
            Title: title,
            Series: series,
            Description: "Live query-bound chart backed by SampleChartSource (IChartSource). Switch chart kind, filter by Q1/Q2, or click any data point.",
            Prefix: "",
            From: "Telemetry"));
    }
}
