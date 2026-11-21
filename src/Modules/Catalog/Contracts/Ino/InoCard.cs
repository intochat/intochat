namespace IntoChat.OS.Modules.Apps;

public sealed record InoChartPoint(
    string Id,
    string Label,
    double Value);

public sealed record InoCard(
    string Id,
    string Kind,
    string Title,
    string Summary,
    string SourceNeuronUri,
    string Status,
    string? SurfaceScopeUri = null,
    string[]? Columns = null,
    string[][]? Rows = null,
    string? ChartKind = null,
    InoChartPoint[]? ChartPoints = null,
    string? Node = null)
{
    public string EffectiveScopeUri => SurfaceScopeUri ?? string.Empty;
    public bool IsWaiting => string.Equals(Status, "pending", StringComparison.OrdinalIgnoreCase)
        && Kind is "approval" or "choice" or "draft" or "secret" or "app-install";
}
