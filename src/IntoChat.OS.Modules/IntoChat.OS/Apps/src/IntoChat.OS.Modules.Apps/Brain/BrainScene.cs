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
    public const int MaxTrailSteps = 10;
    public const string GoldTrailColor = "#FFB800";
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

    public static double TrailOpacity(int step)
        => step < 0 || step >= MaxTrailSteps
            ? 0.0
            : Math.Max(0.0, Math.Round(1.0 - (step * 0.10), 2));

    // Neurons sit on a circle. A synapse is a path between the two. A delivery adds a repeating particle along that path.
    public static SceneDefinition Compose(
        IReadOnlyList<BrainInstance> neurons,
        IReadOnlyList<ObservedSynapse> synapses,
        SignalDelivery? live,
        double zoom)
        => ComposeCore(
            neurons,
            synapses,
            live is null ? [] : [live],
            zoom,
            useGoldTrail: false);

    // 10-step gold trail: newest delivery at index 0 (100% opacity) fading by 10% per step down to step 9 (10% opacity).
    public static SceneDefinition ComposeTrail(
        IReadOnlyList<BrainInstance> neurons,
        IReadOnlyList<ObservedSynapse> synapses,
        IReadOnlyList<SignalDelivery> trail,
        double zoom)
    {
        ArgumentNullException.ThrowIfNull(trail);
        return ComposeCore(neurons, synapses, trail, zoom, useGoldTrail: true);
    }

    private static SceneDefinition ComposeCore(
        IReadOnlyList<BrainInstance> neurons,
        IReadOnlyList<ObservedSynapse> synapses,
        IReadOnlyList<SignalDelivery> trail,
        double zoom,
        bool useGoldTrail)
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
            .ToList();
        // A session synapse names its session neuron. That neuron is not in the instance directory, so the path had no end.
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var neuron in placed)
        {
            known.Add(neuron.Key);
            known.Add(PlaceId(neuron));
        }
        foreach (var end in synapses.SelectMany(row => new[] { row.From, row.To }).Distinct(StringComparer.Ordinal))
        {
            if (placed.Count >= MaxNeurons || known.Contains(end) || !NeuronId.TryParse(end, out var id)) { continue; }
            placed.Add(new BrainInstance(id.Type, id.ToString(), DateTimeOffset.UnixEpoch, null));
            known.Add(id.ToString());
        }
        var at = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        var nodes = new List<SceneNode>(placed.Count + synapses.Count);
        for (var index = 0; index < placed.Count; index++)
        {
            var neuron = placed[index];
            var (x, y) = OnCircle(index, placed.Count);
            // The directory key is brain/key. A synapse names the same neuron as brain/type/key.
            at[neuron.Key] = (x, y);
            at[PlaceId(neuron)] = (x, y);
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
        var activeSteps = Math.Min(trail.Count, MaxTrailSteps);
        foreach (var synapse in synapses.OrderBy(row => row.From, StringComparer.Ordinal).ThenBy(row => row.To, StringComparer.Ordinal).ThenBy(row => row.Signal, StringComparer.Ordinal))
        {
            if (!at.TryGetValue(synapse.From, out var from) || !at.TryGetValue(synapse.To, out var to)) { continue; }
            if (nodes.Count >= FlameVocabulary.MaxNodes - 1) { break; }
            var matchedStep = -1;
            for (var step = 0; step < activeSteps; step++)
            {
                var item = trail[step];
                if (item.From == synapse.From && item.To == synapse.To && item.Signal == synapse.Signal)
                {
                    matchedStep = step;
                    break;
                }
            }

            var lit = matchedStep >= 0;
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["count"] = "12",
                ["speed"] = "0.6",
                ["color"] = useGoldTrail ? GoldTrailColor : "#5EEAD4",
            };
            if (useGoldTrail && lit)
            {
                parameters["step"] = matchedStep.ToString(CultureInfo.InvariantCulture);
                parameters["opacity"] = TrailOpacity(matchedStep).ToString("0.0#", CultureInfo.InvariantCulture);
            }

            SceneNode[] children = lit
                ?
                [
                    new SceneNode(
                        "s" + edge.ToString(CultureInfo.InvariantCulture),
                        "particles",
                        new Transform(from.X, from.Y),
                        null,
                        parameters,
                        [],
                        [
                            new Tween("x", from.X, to.X, 900, "linear", true),
                            new Tween("y", from.Y, to.Y, 900, "linear", true),
                        ],
                        []),
                ]
                : [];
            var pathParameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["d"] = string.Create(CultureInfo.InvariantCulture, $"M {from.X:0.###} {from.Y:0.###} L {to.X:0.###} {to.Y:0.###}"),
            };
            if (useGoldTrail && lit)
            {
                pathParameters["color"] = GoldTrailColor;
                pathParameters["step"] = matchedStep.ToString(CultureInfo.InvariantCulture);
                pathParameters["opacity"] = TrailOpacity(matchedStep).ToString("0.0#", CultureInfo.InvariantCulture);
            }

            nodes.Add(new SceneNode(
                "e" + edge.ToString(CultureInfo.InvariantCulture),
                "path",
                new Transform(),
                null,
                pathParameters,
                children,
                [],
                []));
            edge++;
        }
        return new SceneDefinition(nodes.ToArray(), new SceneCamera(Zoom: zoom <= 0 ? 1 : zoom));
    }

    // A directory row is brain/key. A synapse end is the neuron id brain/type/key. Either string places the same neuron.
    private static string PlaceId(BrainInstance neuron)
    {
        if (NeuronId.TryParse(neuron.Key, out var parsed) && parsed.Type == neuron.Type) { return parsed.ToString(); }
        var slash = neuron.Key.IndexOf('/');
        if (slash > 0 && BrainId.IsValid(neuron.Key[..slash]))
        { return new NeuronId(new BrainId(neuron.Key[..slash]), neuron.Type, neuron.Key[(slash + 1)..]).ToString(); }
        return neuron.Key;
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

[DigitalBrain.Signal("apps.synaptic-pulse")]
public sealed record SynapticPulse(
    string SourceId,
    string TargetId,
    string SignalName,
    DateTimeOffset Timestamp) : DigitalBrain.Signal;
