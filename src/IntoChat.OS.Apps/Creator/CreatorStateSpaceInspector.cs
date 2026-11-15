using System.Text.RegularExpressions;
using DigitalBrain.UI;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Creator;

/// <summary>
/// Accordant-inspired State-Space Inspector for Creator: unfolds both <see cref="CreatorState"/> via
/// <see cref="AggregateExplorer{TState}"/> and static <c>[State]</c>/<c>[Synapse]</c>/<c>Rule</c>/<c>Decision</c>
/// state-machine structure from any inspected unit's C# source files.
/// </summary>
public static partial class CreatorStateSpaceInspector
{
    private static readonly Lazy<StateGraph<CreatorState>> CachedCreatorGraph = new(() =>
        AggregateExplorer<CreatorState>.Explore(maxStates: 16));

    public static StateGraph<CreatorState> CreatorGraph => CachedCreatorGraph.Value;

    public static UnitStateSpaceSummary AnalyzeUnitSource(string alias, IReadOnlyDictionary<string, string> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentNullException.ThrowIfNull(files);

        var csText = string.Join("\n\n", files
            .Where(kv => kv.Key.EndsWith(".cs", StringComparison.Ordinal))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Value));

        if (string.IsNullOrWhiteSpace(csText))
        {
            return new UnitStateSpaceSummary(alias, null, [], [], []);
        }

        string? stateType = null;
        if (StateRecordRegex().Match(csText) is { Success: true } stateMatch)
        {
            stateType = stateMatch.Groups["type"].Value;
        }
        else if (AnyRecordRegex().Match(csText) is { Success: true } recMatch)
        {
            stateType = recMatch.Groups["type"].Value;
        }

        var commands = SynapseReactionRegex().Matches(csText)
            .Select(m => $"{m.Groups["method"].Value} ({m.Groups["signal"].Value})")
            .Concat(DecisionMethodRegex().Matches(csText).Select(m => m.Groups["method"].Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var rules = RuleNameRegex().Matches(csText)
            .Select(m => m.Groups["rule"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var events = EmittedSignalRegex().Matches(csText)
            .Select(m => m.Groups["signal"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new UnitStateSpaceSummary(alias, stateType, commands, rules, events);
    }

    public static Card RenderUnitSummaryCard(string alias, IReadOnlyDictionary<string, string> files)
    {
        var summary = AnalyzeUnitSource(alias, files);
        var creatorGraph = CreatorGraph;

        var subtitle = summary.StateType is { Length: > 0 } st
            ? $"State: {st} · {summary.Commands.Count} transition(s) · {summary.Rules.Count} guard rule(s)"
            : $"Creator Aggregate: {creatorGraph.States.Count} reachable state(s), {creatorGraph.AcceptedTransitions.Count} accepted / {creatorGraph.RejectedTransitions.Count} refused edge(s)";

        var pills = new List<Node>
        {
            StatusPill(creatorGraph.IsHealthy ? StatusKind.Done : StatusKind.Error,
                creatorGraph.IsHealthy ? "Invariants Healthy" : $"{creatorGraph.Violations.Count} Violation(s)"),
        };

        if (summary.Commands.Count > 0)
        {
            pills.Add(StatusPill(StatusKind.Idle, $"{summary.Commands.Count} Command(s)"));
        }

        if (summary.Rules.Count > 0)
        {
            pills.Add(StatusPill(StatusKind.NeedsYou, $"Rules: {string.Join(", ", summary.Rules)}"));
        }

        return (Card)Card("Aggregate State Graph", subtitle)[
            HStack(Space.Sm, [.. pills]),
            summary.Commands.Count > 0
                ? VStack(Space.Xs, [.. summary.Commands.Select(cmd =>
                    MutedText($"{summary.StateType ?? alias} → {cmd} ⇒ {(summary.Events.Count > 0 ? string.Join(", ", summary.Events) : "Store(next)")}"))])
                : MutedText($"Unfolded {creatorGraph.States.Count} reachable state(s), {creatorGraph.AcceptedTransitions.Count} accepted & {creatorGraph.RejectedTransitions.Count} rule-refused self-loop transition(s).")
        ].ToNode();
    }

    public static BrainSceneNode RenderUnitStateScene(string alias, IReadOnlyDictionary<string, string> files)
    {
        var summary = AnalyzeUnitSource(alias, files);
        if (summary.Commands.Count == 0 && summary.StateType is null)
        {
            return StateGraphView(CreatorGraph);
        }

        var stateNodeId = $"state:{summary.StateType ?? alias}";
        var nodes = new List<BrainGraphNode>
        {
            new(
                Id: stateNodeId,
                Label: summary.StateType ?? alias,
                Uri: $"state://{alias}/{stateNodeId}",
                Kind: "initial",
                X: 0,
                Y: 0,
                Z: 0,
                Status: "done",
                Subtitle: $"{summary.Commands.Count} transition(s)"),
        };
        var synapses = new List<BrainGraphSynapse>();

        for (var i = 0; i < summary.Commands.Count; i++)
        {
            var cmd = summary.Commands[i];
            var nextId = $"next:{i}:{cmd}";
            var centeredY = (i - ((summary.Commands.Count - 1) / 2.0)) * 96.0;
            nodes.Add(new BrainGraphNode(
                Id: nextId,
                Label: cmd,
                Uri: $"state://{alias}/{nextId}",
                Kind: "state",
                X: 200,
                Y: centeredY,
                Z: 0,
                Status: "idle",
                Subtitle: summary.Events.Count > 0 ? string.Join(", ", summary.Events) : "Store(next)"));
            synapses.Add(new BrainGraphSynapse(stateNodeId, nextId, cmd, Active: true));
        }

        foreach (var rule in summary.Rules)
        {
            synapses.Add(new BrainGraphSynapse(stateNodeId, stateNodeId, $"✗ {rule}", Active: false));
        }

        return new BrainSceneNode(nodes, synapses, stateNodeId)
        {
            PauseRotation = true,
        };
    }

    [GeneratedRegex(@"\[State\([^\)]*\)\]\s*public\s+(?:sealed\s+)?record\s+(?<type>[A-Za-z0-9_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex StateRecordRegex();

    [GeneratedRegex(@"public\s+sealed\s+record\s+(?<type>[A-Za-z0-9_]*State)\b", RegexOptions.CultureInvariant)]
    private static partial Regex AnyRecordRegex();

    [GeneratedRegex(@"\[Synapse\(\s*(?:typeof\((?<signal>[A-Za-z0-9_]+)\)|""(?<signal>[^""]+)"")[^\]]*\)\]\s*public\s+static\s+[^\s]+\s+(?<method>[A-Za-z0-9_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SynapseReactionRegex();

    [GeneratedRegex(@"public\s+Decision<[^>]+>\s+(?<method>[A-Za-z0-9_]+)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex DecisionMethodRegex();

    [GeneratedRegex(@"Decision\.Rule\(\s*""(?<rule>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex RuleNameRegex();

    [GeneratedRegex(@"\[Signal\(\s*""(?<signal>[^""]+)""\s*\)\]", RegexOptions.CultureInvariant)]
    private static partial Regex EmittedSignalRegex();
}

public sealed record UnitStateSpaceSummary(
    string Alias,
    string? StateType,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> Rules,
    IReadOnlyList<string> Events);
