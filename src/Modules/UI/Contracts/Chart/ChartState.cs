namespace IntoChat.OS.Modules.UI.Chart;

/// <summary>Supported visualization kinds for <see cref="IChart"/>.</summary>
public static class ChartKinds
{
    public const string Columns = "columns";
    public const string Line = "line";
    public const string Area = "area";
    public const string Pie = "pie";

    public static readonly IReadOnlyList<string> All = [Columns, Line, Area, Pie];

    public static string Normalize(string? kind) => (kind ?? Columns).Trim().ToLowerInvariant() switch
    {
        "bar" or "bars" or "column" or "columns" => Columns,
        "line" => Line,
        "area" => Area,
        "pie" or "donut" => Pie,
        _ => throw new ArgumentException($"Unknown chart kind '{kind}'. Supported kinds: columns, line, area, pie.", nameof(kind)),
    };
}

/// <summary>A single labelled data point in a chart series.</summary>
[GenerateSerializer, Alias("ui.chart.point")]
public sealed record ChartPoint(
    [property: Id(0)] string Label,
    [property: Id(1)] double Value,
    [property: Id(2)] string? EventId = null)
{
    public ChartPoint() : this("", 0d, null) { }
}

/// <summary>A named series of data points.</summary>
[GenerateSerializer, Alias("ui.chart.series")]
public sealed record ChartSeries(
    [property: Id(0)] string Name,
    [property: Id(1)] ChartPoint[] Points)
{
    public ChartSeries() : this("Primary", []) { }
}

/// <summary>Query specification used when a chart is bound to an <see cref="IChartSource"/> neuron.</summary>
[GenerateSerializer, Alias("ui.chart.query")]
public sealed record ChartQuery(
    [property: Id(0)] string? Filter = null,
    [property: Id(1)] string? GroupBy = null,
    [property: Id(2)] string? Range = null,
    [property: Id(3)] int? Limit = null)
{
    public ChartQuery() : this(null, null, null, null) { }
}

/// <summary>Selected point on a chart for drill-down or cross-filtering.</summary>
[GenerateSerializer, Alias("ui.chart.selection")]
public sealed record ChartSelection(
    [property: Id(0)] string Label,
    [property: Id(1)] string SeriesName,
    [property: Id(2)] double Value)
{
    public ChartSelection() : this("", "", 0d) { }
}

/// <summary>Input specification for replacing a chart's data snapshot via <see cref="IChart.Render"/>.</summary>
[GenerateSerializer, Alias("ui.chart.spec")]
public sealed record ChartSpec(
    [property: Id(0)] string Title,
    [property: Id(1)] string Kind,
    [property: Id(2)] IReadOnlyList<ChartSeries> Series,
    [property: Id(3)] string Description = "",
    [property: Id(4)] string Prefix = "",
    [property: Id(5)] string From = "DigitalBrain")
{
    public ChartSpec() : this("", ChartKinds.Columns, [], "", "", "DigitalBrain") { }
}

/// <summary>Result returned by an <see cref="IChartSource"/> neuron when queried by <see cref="IChart"/>.</summary>
[GenerateSerializer, Alias("ui.chart.source-result")]
public sealed record ChartSourceResult(
    [property: Id(0)] string Title,
    [property: Id(1)] IReadOnlyList<ChartSeries> Series,
    [property: Id(2)] string Description = "",
    [property: Id(3)] string Prefix = "",
    [property: Id(4)] string From = "DigitalBrain")
{
    public ChartSourceResult() : this("", [], "", "", "DigitalBrain") { }
}

/// <summary>Durable state of an <see cref="IChart"/> neuron.</summary>
[GenerateSerializer, Alias("ui.chart.state")]
public sealed record ChartState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Title,
    [property: Id(2)] string Description,
    [property: Id(3)] string Kind,
    [property: Id(4)] ChartSeries[] Series,
    [property: Id(5)] string? SourceNeuronId,
    [property: Id(6)] ChartQuery? Query,
    [property: Id(7)] ChartSelection? SelectedPoint,
    [property: Id(8)] string Status,
    [property: Id(9)] string? Error,
    [property: Id(10)] string Prefix,
    [property: Id(11)] string From)
{
    public const int MaxPointsPerSeries = 200;

    public ChartState() : this(
        Version: 0,
        Title: "",
        Description: "",
        Kind: ChartKinds.Columns,
        Series: [],
        SourceNeuronId: null,
        Query: null,
        SelectedPoint: null,
        Status: "empty",
        Error: null,
        Prefix: "",
        From: "DigitalBrain")
    {
    }
}
