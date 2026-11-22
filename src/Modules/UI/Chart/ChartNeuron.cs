using IntoChat.OS.Modules.UI.Core;
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.Chart.Signals;

namespace IntoChat.OS.Modules.UI.Chart;

[Neuron("ui.chart")]
internal sealed partial class ChartNeuron : UiNeuronBase<ChartState>, IChart
{
    [DigitalBrain.Slot(Required = false)]
    private partial DigitalBrain.Components.SlotCollection<IChartSource> ChartSources { get; }
    public async Task<ChartState> Render(ChartSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var kind = ChartKinds.Normalize(spec.Kind);
        var series = NormalizeSeries(spec.Series);
        var next = State with
        {
            Version = State.Version + 1,
            Title = spec.Title ?? "",
            Description = spec.Description ?? "",
            Kind = kind,
            Series = series,
            SelectedPoint = null,
            Status = TotalPoints(series) > 0 ? "ready" : "empty",
            Error = null,
            Prefix = spec.Prefix ?? "",
            From = string.IsNullOrWhiteSpace(spec.From) ? "DigitalBrain" : spec.From,
        };
        await Save(next, Changed(next));
        return next;
    }

    public async Task<ChartState> Append(ChartPoint point, string? seriesName = null)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentException.ThrowIfNullOrWhiteSpace(point.Label);

        var targetName = string.IsNullOrWhiteSpace(seriesName)
            ? (State.Series.Length > 0 ? State.Series[0].Name : "Primary")
            : seriesName.Trim();

        var list = State.Series.ToList();
        var index = list.FindIndex(s => string.Equals(s.Name, targetName, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            var existing = list[index];
            if (!string.IsNullOrWhiteSpace(point.EventId)
                && existing.Points.Any(p => string.Equals(p.EventId, point.EventId, StringComparison.Ordinal)))
            {
                return State;
            }
            var updatedPoints = existing.Points.Append(point).TakeLast(ChartState.MaxPointsPerSeries).ToArray();
            list[index] = existing with { Points = updatedPoints };
        }
        else
        {
            list.Add(new ChartSeries(targetName, [point]));
        }

        var next = State with
        {
            Version = State.Version + 1,
            Series = [.. list],
            Status = "ready",
            Error = null,
        };
        await Save(next, Changed(next));
        return next;
    }

    public async Task<ChartState> SetKind(string kind)
    {
        var normalized = ChartKinds.Normalize(kind);
        if (string.Equals(State.Kind, normalized, StringComparison.Ordinal))
        {
            return State;
        }
        var next = State with
        {
            Version = State.Version + 1,
            Kind = normalized,
        };
        await Save(next, Changed(next));
        return next;
    }

    public async Task<ChartState> SelectPoint(string? label, string? seriesName = null)
    {
        ChartSelection? selection = null;
        if (!string.IsNullOrWhiteSpace(label))
        {
            var trimmed = label.Trim();
            var series = !string.IsNullOrWhiteSpace(seriesName)
                ? State.Series.FirstOrDefault(s => string.Equals(s.Name, seriesName.Trim(), StringComparison.OrdinalIgnoreCase))
                : State.Series.FirstOrDefault(s => s.Points.Any(p => string.Equals(p.Label, trimmed, StringComparison.OrdinalIgnoreCase)));
            var point = series?.Points.FirstOrDefault(p => string.Equals(p.Label, trimmed, StringComparison.OrdinalIgnoreCase));
            if (series is not null && point is not null)
            {
                selection = new ChartSelection(point.Label, series.Name, point.Value);
            }
        }

        var next = State with
        {
            Version = State.Version + 1,
            SelectedPoint = selection,
        };
        await Save(next, Changed(next), new ChartPointSelected(Id.FullKey(), selection?.Label, selection?.SeriesName, selection?.Value));
        return next;
    }

    public async Task<ChartState> SetDocs(string description)
    {
        var next = State with
        {
            Version = State.Version + 1,
            Description = description ?? "",
        };
        await Save(next, Changed(next));
        return next;
    }

    public async Task<ChartState> Bind(string sourceNeuronId, ChartQuery query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceNeuronId);
        ArgumentNullException.ThrowIfNull(query);
        var bound = State with
        {
            SourceNeuronId = sourceNeuronId.Trim(),
            Query = query,
        };
        return await ExecuteBoundQuery(bound);
    }

    public Task<ChartState> Refresh()
    {
        if (string.IsNullOrWhiteSpace(State.SourceNeuronId))
        {
            return Task.FromResult(State);
        }
        return ExecuteBoundQuery(State);
    }

    public Task<ChartState> Read() => Task.FromResult(State);

    private async Task<ChartState> ExecuteBoundQuery(ChartState basis)
    {
        var sourceId = basis.SourceNeuronId!;
        if (!NeuronId.TryParse(sourceId, out var address) || !ChartSources.Exists(address))
        {
            var invalid = basis with
            {
                Version = basis.Version + 1,
                Status = "error",
                Error = $"Source '{sourceId}' is not a valid IChartSource in this brain.",
            };
            await Save(invalid, Changed(invalid));
            return invalid;
        }

        try
        {
            var result = await ChartSources[address].Query(basis.Query ?? new ChartQuery());
            var series = NormalizeSeries(result.Series);
            var next = basis with
            {
                Version = basis.Version + 1,
                Title = string.IsNullOrWhiteSpace(result.Title) ? basis.Title : result.Title,
                Description = string.IsNullOrWhiteSpace(result.Description) ? basis.Description : result.Description,
                Series = series,
                Prefix = result.Prefix ?? basis.Prefix,
                From = string.IsNullOrWhiteSpace(result.From) ? basis.From : result.From,
                Status = TotalPoints(series) > 0 ? "ready" : "empty",
                Error = null,
            };
            await Save(next, Changed(next));
            return next;
        }
#pragma warning disable CA1031 // Bound data source failure becomes chart error state.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            GetLogger().LogSwallowed(ex, "Bound chart source query failed.");
            var failed = basis with
            {
                Version = basis.Version + 1,
                Status = "error",
                Error = ex.Message,
            };
            await Save(failed, Changed(failed));
            return failed;
        }
    }

    private static ChartSeries[] NormalizeSeries(IReadOnlyList<ChartSeries>? series)
    {
        if (series is null || series.Count == 0) { return []; }
        return
        [
            .. series.Select(s => new ChartSeries(
                string.IsNullOrWhiteSpace(s.Name) ? "Primary" : s.Name.Trim(),
                [.. (s.Points ?? []).TakeLast(ChartState.MaxPointsPerSeries)])),
        ];
    }

    private static int TotalPoints(IReadOnlyList<ChartSeries> series)
        => series.Sum(s => s.Points.Length);

    private UiChartChanged Changed(ChartState state)
        => new(Id.FullKey(), state.Version, state.Kind, TotalPoints(state.Series), state.Status);
}
