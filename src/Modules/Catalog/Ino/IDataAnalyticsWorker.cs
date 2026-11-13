using Orleans;
using Orleans.CodeGeneration;
using Orleans.Concurrency;

namespace IntoChat.OS.Modules.Apps;

[GenerateSerializer, Alias("ino.chart-point.surrogate")]
internal record struct InoChartPointSurrogate(
    [property: Id(0)] string Id,
    [property: Id(1)] string Label,
    [property: Id(2)] double Value);

[RegisterConverter]
internal sealed class InoChartPointConverter : IConverter<InoChartPoint, InoChartPointSurrogate>
{
    public InoChartPoint ConvertFromSurrogate(in InoChartPointSurrogate surrogate)
        => new(surrogate.Id, surrogate.Label, surrogate.Value);

    public InoChartPointSurrogate ConvertToSurrogate(in InoChartPoint value)
        => new(value.Id, value.Label, value.Value);
}

[GenerateSerializer, Alias("ino.data-query-result")]
public sealed record DataQueryResult(
    [property: Id(0)] string ChartTitle,
    [property: Id(1)] string[] Columns,
    [property: Id(2)] string[][] Rows,
    [property: Id(3)] string ChartKind,
    [property: Id(4)] InoChartPoint[] ChartPoints,
    [property: Id(5)] string Summary,
    [property: Id(6)] string SourceUri,
    [property: Id(7)] double TotalValue,
    [property: Id(8)] double AverageValue,
    [property: Id(9)] double MinValue,
    [property: Id(10)] double MaxValue);

[Alias("ino.data-analytics-worker")]
[Version(1)]
public interface IDataAnalyticsWorker : IGrainWithIntegerKey
{
    [AlwaysInterleave]
    Task<DataQueryResult> ExecuteAsync(string queryOrPrompt, string database, string? fileDatasetText = null, string? fileDatasetPath = null);
}
