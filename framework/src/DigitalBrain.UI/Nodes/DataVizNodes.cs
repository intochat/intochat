using System.ComponentModel;
using System.Text.Json.Serialization;

namespace DigitalBrain.UI;

[ViewNode("dataTable")]
public sealed record DataTable(
    string Title,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    string? SourceNeuronUri = null,
    string From = "DigitalBrain") : Node
{
    /// <inheritdoc />
    public bool Equals(DataTable? other) =>
        other is not null &&
        Title == other.Title &&
        SourceNeuronUri == other.SourceNeuronUri &&
        From == other.From &&
        Columns.SequenceEqual(other.Columns) &&
        Rows.Count == other.Rows.Count &&
        Rows.Zip(other.Rows).All(pair => pair.First.SequenceEqual(pair.Second));

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Title, Columns.Count, Rows.Count);
}

/// <summary>A single data point in a <see cref="Chart"/>.</summary>
public sealed record ChartPointNode(string Label, double Value);

/// <summary>Single-series or period-comparison chart card (<c>columns</c> or <c>line</c>).</summary>
[ViewNode("chart")]
public sealed record Chart(
    [property: JsonPropertyName("chartKind")] string Kind,
    string Title,
    IReadOnlyList<ChartPointNode> Points,
    IReadOnlyList<double>? ComparePoints = null,
    string SeriesName = "",
    string CompareName = "",
    string? SourceNeuronUri = null,
    string From = "DigitalBrain",
    string Prefix = "") : Node
{
    /// <inheritdoc />
    public bool Equals(Chart? other) =>
        other is not null &&
        Kind == other.Kind &&
        Title == other.Title &&
        SeriesName == other.SeriesName &&
        CompareName == other.CompareName &&
        SourceNeuronUri == other.SourceNeuronUri &&
        From == other.From &&
        Prefix == other.Prefix &&
        Points.SequenceEqual(other.Points) &&
        ((ComparePoints is null && other.ComparePoints is null) ||
         (ComparePoints is not null && other.ComparePoints is not null && ComparePoints.SequenceEqual(other.ComparePoints)));

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Kind, Title, Points.Count);
}



/// <summary>A 3D neuron node in a <see cref="BrainSceneNode"/>.</summary>
public sealed record BrainGraphNode(
    string Id,
    string Label,
    string Uri,
    string Kind,
    double X,
    double Y,
    double Z,
    string Status = "idle",
    string? Subtitle = null,
    Button? Select = null);

/// <summary>A directed synapse edge in a <see cref="BrainSceneNode"/>.</summary>
public sealed record BrainGraphSynapse(
    string FromId,
    string ToId,
    string? SignalLabel = null,
    bool Active = false);

/// <summary>Interactive 3D neural topology scene.</summary>
[ViewNode("brainScene")]
public sealed record BrainSceneNode(
    IReadOnlyList<BrainGraphNode> Nodes,
    IReadOnlyList<BrainGraphSynapse> Synapses,
    string? SelectedId = null) : Node
{
    /// <summary>True to pause orbital auto-rotation when inspecting or creating a target app node.</summary>
    public bool PauseRotation { get; init; }

    /// <summary>Target 3D node ID to smoothly center and zoom the camera into.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ZoomTargetId { get; init; }

    /// <summary>Target camera zoom factor (default <c>1.0</c>).</summary>
    public double TargetZoom { get; init; } = 1.0;

    /// <inheritdoc />
    public bool Equals(BrainSceneNode? other) =>
        other is not null &&
        SelectedId == other.SelectedId &&
        PauseRotation == other.PauseRotation &&
        ZoomTargetId == other.ZoomTargetId &&
        TargetZoom.Equals(other.TargetZoom) &&
        Nodes.SequenceEqual(other.Nodes) &&
        Synapses.SequenceEqual(other.Synapses);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(SelectedId, PauseRotation, ZoomTargetId, Nodes.Count, Synapses.Count);
}

/// <summary>Declarative WASM / web studio viewport embedded inside an app view.</summary>
[ViewNode("webBrowser")]
public sealed record WebBrowser(
    string Key,
    string Url,
    string? Title = null,
    double Height = 520,
    string ExecutionMode = "wasm-ui",
    string AutomationName = "",
    string Chrome = "full") : Node
{
    /// <summary>The accessible name: the given one, or <see cref="Title"/> / <see cref="Key"/> when none was given.</summary>
    public string AutomationName { get; init; } = !string.IsNullOrWhiteSpace(AutomationName) ? AutomationName : !string.IsNullOrWhiteSpace(Title) ? Title! : Key ?? "";
}

/// <summary>Reference to a live neuron control in the same brain.</summary>
/// <param name="Kind">How the client hosts it ("slot" for a child of the view's screen).</param>
/// <param name="NeuronUri">The neuron.</param>
/// <param name="Props">A slot's props: the setup its parent decides (a <see cref="ControlProps"/> record as JSON); null for none.</param>
[ViewNode("neuronRef")]
public sealed record NeuronRef([property: JsonPropertyName("refKind")] string Kind, string NeuronUri,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] System.Text.Json.JsonElement? Props = null) : Node;
