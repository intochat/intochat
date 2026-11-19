namespace DigitalBrain.UI.Controls;

/// <summary>The state of a live chart neuron: the points it holds (the last 200) and the change counter.</summary>
public sealed record LiveChartState
{
    /// <summary>The most points a live chart holds; older ones are dropped.</summary>
    public const int MaxPoints = 200;

    /// <summary>How many changes the chart has seen.</summary>
    public int Version { get; init; }

    /// <summary>The held points, oldest first.</summary>
    [Showcase("""{"kind":"liveChartView","key":"spend","version":2,"points":[{"label":"Mon","value":12},{"label":"Tue","value":30},{"label":"Wed","value":21}],"props":{"title":"Daily spend","kind":"bar","isEnabled":true,"automationName":"Daily spend"}}""")]
    [Showcase("""{"kind":"liveChartView","key":"share","version":1,"points":[{"label":"Chat","value":60},{"label":"Files","value":40}],"props":{"title":"Share of use","kind":"pie","isEnabled":true,"automationName":"Share of use"}}""")]
    public IReadOnlyList<LivePoint> Points { get; init; } = [];

    /// <inheritdoc />
    public bool Equals(LiveChartState? other) => other is not null && Version == other.Version && Points.SequenceEqual(other.Points);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Version, Points.Count);
}

/// <summary>What a live chart neuron draws: its key, change counter and points (labels and values). The client draws the title and kind from the slot's props.</summary>
/// <param name="Key">The neuron's key, by which the client finds the control.</param>
/// <param name="Version">How many changes the chart has seen.</param>
/// <param name="Points">The held points, oldest first.</param>
[ViewNode("liveChartView")]
public sealed record LiveChartView(string Key, int Version, IReadOnlyList<ChartPointNode> Points) : Node
{
    /// <inheritdoc />
    public bool Equals(LiveChartView? other) => other is not null && Key == other.Key && Version == other.Version && Points.SequenceEqual(other.Points);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Key, Version, Points.Count);
}
