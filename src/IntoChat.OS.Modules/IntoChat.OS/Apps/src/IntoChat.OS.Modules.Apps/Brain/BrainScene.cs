/*
# Brain
Role: Default
Shipped: true
Operations: open

The brain app draws the current brain: one sphere for each neuron and one path for each synapse.
A delivered signal runs particles along that path. A tap zooms the camera.

## Scenario: Opening shows the graph
Invoking "open" places the caller's neurons and synapses on the scene.
## Scenario: A delivery moves particles
A signal delivered along a synapse tweens particles from its publisher to its subscriber.
## Scenario: A tap zooms
A tap on the scene raises the camera zoom until it reaches the cap.
*/
using System.Globalization;
using DigitalBrain;
using DigitalBrain.Spi;
using IntoChat.OS.Modules.Flame;
using IntoChat.OS.Modules.Flame.Scene;

namespace IntoChat.OS.Modules.Apps;

// The brain app: a static graph of the caller's neurons and synapses, plus a particle on the path a signal just took.
[DigitalBrain.App("brain")]
public sealed class BrainScene : DigitalBrain.App
{
    public const string SceneName = "graph";
    public const double ZoomCap = 4;
    public const double ZoomStep = 1.5;
    private const double Radius = 240;
    private const int MaxNeurons = 1024;

    public static DigitalBrain.AppManifest Manifest { get; } = new(
        "brain", "Brain", "1.0.0", [], new DigitalBrain.AppRequirements([]));

    public static AppBundle Bundle { get; } = new(
        "brain", "1.0.0",
        new AppManifest(
            Title: "Brain",
            Description: "The neurons and synapses of this brain.",
            Operations: [new("open", "Show the brain.")],
            Settings: [],
            Slots: []),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal));

    [DigitalBrain.On]
    public Task Open(BrainSceneOpen request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public static double NextZoom(double zoom) => Math.Min(ZoomCap, (zoom <= 0 ? 1 : zoom) * ZoomStep);

    // Neurons sit on a circle. A synapse is a path between the two. A delivery adds a repeating particle along that path.
    public static SceneDefinition Compose(
        IReadOnlyList<BrainInstance> neurons,
        IReadOnlyList<ObservedSynapse> synapses,
        SignalDelivery? live,
        double zoom)
    {
        ArgumentNullException.ThrowIfNull(neurons);
        ArgumentNullException.ThrowIfNull(synapses);
        var placed = neurons
            .Where(neuron => neuron.Key.Length > 0 && neuron.Type.Length > 0)
            .GroupBy(neuron => neuron.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(neuron => neuron.Type, StringComparer.Ordinal)
            .ThenBy(neuron => neuron.Key, StringComparer.Ordinal)
            .Take(MaxNeurons)
            .ToArray();
        var at = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        var nodes = new List<SceneNode>(placed.Length + synapses.Count);
        for (var index = 0; index < placed.Length; index++)
        {
            var neuron = placed[index];
            var (x, y) = OnCircle(index, placed.Length);
            at[neuron.Key] = (x, y);
            var label = neuron.Type + " " + neuron.Key;
            nodes.Add(new SceneNode(
                "n" + index.ToString(CultureInfo.InvariantCulture),
                "sphere",
                new Transform(x, y),
                null,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["icon"] = "hub", ["title"] = label },
                [],
                [],
                ["tap"]));
        }
        var edge = 0;
        foreach (var synapse in synapses.OrderBy(row => row.From, StringComparer.Ordinal).ThenBy(row => row.To, StringComparer.Ordinal).ThenBy(row => row.Signal, StringComparer.Ordinal))
        {
            if (!at.TryGetValue(synapse.From, out var from) || !at.TryGetValue(synapse.To, out var to)) { continue; }
            if (nodes.Count >= FlameVocabulary.MaxNodes - 1) { break; }
            var lit = live is not null
                && live.From == synapse.From && live.To == synapse.To && live.Signal == synapse.Signal;
            SceneNode[] children = lit
                ?
                [
                    new SceneNode(
                        "s" + edge.ToString(CultureInfo.InvariantCulture),
                        "particles",
                        new Transform(from.X, from.Y),
                        null,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["count"] = "12",
                            ["speed"] = "0.6",
                            ["color"] = "#5EEAD4",
                        },
                        [],
                        [
                            new Tween("x", from.X, to.X, 900, "linear", true),
                            new Tween("y", from.Y, to.Y, 900, "linear", true),
                        ],
                        []),
                ]
                : [];
            nodes.Add(new SceneNode(
                "e" + edge.ToString(CultureInfo.InvariantCulture),
                "path",
                new Transform(),
                null,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["d"] = string.Create(CultureInfo.InvariantCulture, $"M {from.X:0.###} {from.Y:0.###} L {to.X:0.###} {to.Y:0.###}"),
                },
                children,
                [],
                []));
            edge++;
        }
        return new SceneDefinition(nodes.ToArray(), new SceneCamera(Zoom: zoom <= 0 ? 1 : zoom));
    }

    private static (double X, double Y) OnCircle(int index, int count)
    {
        if (count <= 1) { return (0, 0); }
        var angle = (Math.PI * 2 * index / count) - (Math.PI / 2);
        return (Math.Cos(angle) * Radius, Math.Sin(angle) * Radius);
    }
}

[DigitalBrain.Signal("apps.brain-opened")]
public sealed record BrainSceneOpen : DigitalBrain.Signal;
