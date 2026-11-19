using DigitalBrain;

namespace IntoChat.OS.Modules.UI.Chart.Signals;

[Signal("ui.chart-changed")]
public sealed record UiChartChanged(
    string ChartId,
    int Version,
    string Kind,
    int PointCount,
    string Status) : Signal;
