/*
# Live chart

A control neuron: a chart of points a feed adds to. It owns the points (the last 200) and a change counter, and signals ChartChanged
on every change. Its title and kind (bar, line, area, pie) are props its parent writes on the child node; the parent refuses Replace and
Append while the kind is unknown (control.unknownKind) and the client draws an error instead of a chart. An Append whose event id the
chart still holds is ignored (a trimmed event id may be appended again). Named LiveChart because a `chart` data node already exists;
it draws as a liveChartView.
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A live chart: it owns the points and signals <see cref="ChartChanged"/>.</summary>
[Neuron("control.liveChart"), Publishes(typeof(ChartChanged))]
public sealed class LiveChartNeuron : ControlNeuron<LiveChartState, LiveChartProps>, ILiveChartControl
{
    [On]
    public Task Replace(Replace command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Points);
        return Change([.. command.Points.TakeLast(LiveChartState.MaxPoints)]);
    }

    [On]
    public Task Append(Append command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var point = command.Point ?? throw new ArgumentException("A point is required.", nameof(command));
        ArgumentException.ThrowIfNullOrWhiteSpace(point.Label);
        if (!string.IsNullOrWhiteSpace(point.EventId) && State.Points.Any(held => held.EventId == point.EventId))
        {
            return Task.CompletedTask;
        }
        return Change([.. State.Points.Append(point).TakeLast(LiveChartState.MaxPoints)]);
    }

    private Task Change(IReadOnlyList<LivePoint> points)
    {
        var version = State.Version + 1;
        return Show(State with { Version = version, Points = points }, new ChartChanged(version));
    }

    protected override View Render(LiveChartState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(Key, new LiveChartView(Key, state.Version, [.. state.Points.Select(point => new ChartPointNode(point.Label, point.Value))]));
    }
}
