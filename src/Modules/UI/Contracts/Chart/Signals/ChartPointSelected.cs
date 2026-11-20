using DigitalBrain;

namespace IntoChat.OS.Modules.UI.Chart.Signals;

[Signal("ui.chart-point-selected")]
public sealed record ChartPointSelected(
    string ChartId,
    string? Label,
    string? SeriesName,
    double? Value) : Signal;
