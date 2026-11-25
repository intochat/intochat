using System.Text.Json.Serialization;
using DigitalBrain.Rules;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.Creator;

public sealed record CreatorWorkspaceState(
    Selection? Selected,
    string? TraceId,
    long? ReplayCursor,
    string InspectorTab,
    string CanvasMode,
    string PreviewViewport,
    string Status)
{
    public static readonly string[] InspectorTabIds = ["state", "source", "signals", "state-graph", "scenarios"];
    public static readonly string[] CanvasModeIds = ["brain", "state", "live"];

    public CreatorWorkspaceState() : this(null, null, null, "state", "brain", "fill", "Ready") { }

    public string? Transcript { get; init; }
    public string? ActiveRunId { get; init; }
    public string? SelectedSourceFile { get; init; }
    public int ReplayStepCount { get; init; }
    public bool ShowStateGraph { get; init; }
    public bool ScenarioInFlight { get; init; }
    public Inspection Inspection { get; init; } = Inspection.None;

    [JsonIgnore]
    public bool IsReplayActive => ReplayCursor is not null;

    // ── Domain Invariants & Rules ────────────────────────────────────────────

    [Invariant("ReplayCursor is null (live) or within non-negative timeline bounds."), JsonIgnore]
    public bool ValidReplayCursor =>
        ReplayCursor is null || (ReplayCursor >= 0 && (ReplayStepCount == 0 || ReplayCursor < ReplayStepCount));

    [Invariant("InspectorTab is one of the 5 canonical Creator inspector tabs."), JsonIgnore]
    public bool ValidInspectorTab =>
        InspectorTab is "state" or "source" or "signals" or "state-graph" or "scenarios";

    [Invariant("CanvasMode is brain, state, or live."), JsonIgnore]
    public bool ValidCanvasMode =>
        CanvasMode is "brain" or "state" or "live";

    [Invariant("PreviewViewport is fill, 720, or 390."), JsonIgnore]
    public bool ValidPreviewViewport =>
        PreviewViewport is "fill" or "720" or "390";

    [Invariant("ShowStateGraph flag stays synchronized with state CanvasMode."), JsonIgnore]
    public bool ConsistentStateGraphFlag =>
        ShowStateGraph == (CanvasMode == "state");

    [JsonIgnore]
    public Rule RequiresSelectedNode => Decision.Rule(
        "requires_selected_node",
        Selected,
        static s => s is not { Kind: Selection.Node },
        "Select a unit or app node in the graph first.");

    public Rule CanDepart(bool force = false) => Decision.Rule(
        "scenario_run_in_progress",
        (ScenarioInFlight, force),
        static x => x.ScenarioInFlight && !x.force,
        "Wait for the active scenario verification run to finish before leaving Creator.");

    public static Rule RequiresTimelineEvents(int eventCount) => Decision.Rule(
        "empty_timeline",
        eventCount,
        static n => n <= 0,
        "No recorded signal events are loaded for time-travel.");

    public static Rule SupportedInspectorTab(string? tab) => Decision.Rule(
        "invalid_inspector_tab",
        tab,
        static t => TryNormalizeInspectorTab(t) is null,
        "Inspector tab must be state, source, signals, state-graph, or scenarios.");

    public static Rule SupportedCanvasMode(string? mode) => Decision.Rule(
        "invalid_canvas_mode",
        mode,
        static m => TryNormalizeCanvasMode(m) is null,
        "Canvas mode must be brain, state, or live.");

    // ── Aggregate Commands & Transitions ─────────────────────────────────────

    public CreatorWorkspaceState SelectNode([Sample("local/ctr/default")] string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        var parsed = Selection.Parse(nodeId);
        return this with
        {
            Selected = parsed,
            Transcript = null,
            SelectedSourceFile = null,
            ReplayCursor = null,
            ReplayStepCount = 0,
            Status = $"Selected {parsed?.Id ?? nodeId}",
        };
    }

    public CreatorWorkspaceState SelectRun([Sample("run-1")] string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return this with
        {
            ActiveRunId = runId.Trim(),
            Status = $"Inspecting flow {runId.Trim()}",
        };
    }

    public CreatorWorkspaceState ToggleStateGraph()
    {
        var nextShow = !ShowStateGraph;
        return this with
        {
            ShowStateGraph = nextShow,
            CanvasMode = nextShow ? "state" : "brain",
            Status = nextShow ? "Showing Accordant State Graph" : "Showing Live Brain Topology",
        };
    }

    public CreatorWorkspaceState ClearSelection() => this with
    {
        Selected = null,
        Inspection = Inspection.None,
        ActiveRunId = null,
        Transcript = null,
        SelectedSourceFile = null,
        ReplayCursor = null,
        ReplayStepCount = 0,
        Status = "Ready",
    };

    public Decision<CreatorWorkspaceState> SelectInspectorTab(
        [Sample("state", "source", "signals", "state-graph", "scenarios", "invalid")] string tab)
    {
        if (Decision.Check("select-inspector-tab", SupportedInspectorTab(tab)) is { } refusal)
        {
            return refusal;
        }

        var normalized = TryNormalizeInspectorTab(tab)!;
        return Decision.Accept(this with
        {
            InspectorTab = normalized,
            Status = $"Inspector · {FormatInspectorTabTitle(normalized)}",
        });
    }

    public Decision<CreatorWorkspaceState> SwitchCanvasMode(
        [Sample("brain", "state", "live", "invalid")] string mode)
    {
        if (Decision.Check("switch-canvas-mode", SupportedCanvasMode(mode)) is { } refusal)
        {
            return refusal;
        }

        var normalized = TryNormalizeCanvasMode(mode)!;
        return Decision.Accept(this with
        {
            CanvasMode = normalized,
            ShowStateGraph = normalized == "state",
            Status = $"Canvas · {normalized}",
        });
    }

    public Decision<CreatorWorkspaceState> StepTimeline(
        [Sample("next", "prev", "toggle", "stop")] string cursorArg,
        [Sample(0, 4)] int eventCount)
    {
        var clean = cursorArg?.Trim() ?? string.Empty;
        if (clean is "stop" or "live")
        {
            return Decision.Accept(this with
            {
                ReplayCursor = null,
                ReplayStepCount = Math.Max(0, eventCount),
                Status = ReplayScript.FormatCursorReadout(null, Math.Max(0, eventCount)),
            });
        }

        if (Decision.Check("step-timeline", RequiresTimelineEvents(eventCount)) is { } refusal)
        {
            return refusal;
        }

        var nextCursor = ReplayScript.Move(ReplayCursor, clean, eventCount);
        return Decision.Accept(this with
        {
            ReplayCursor = nextCursor,
            ReplayStepCount = eventCount,
            Status = ReplayScript.FormatCursorReadout(nextCursor, eventCount),
        });
    }

    public Decision<CreatorWorkspaceState> ValidateUnitSelection()
        => Decision.Check("unit-action", RequiresSelectedNode) ?? Decision.Accept(this);

    // ── Non-explored Runtime Helpers ─────────────────────────────────────────

    [IgnoreExplore]
    public CreatorWorkspaceState SelectSelection(Selection? selection, int stepCount = 0)
    {
        var defaultTab = selection?.Kind switch
        {
            Selection.Pulse or Selection.Edge => "signals",
            _ => InspectorTab,
        };
        return this with
        {
            Selected = selection,
            TraceId = selection?.Kind == Selection.Pulse ? selection.Id : TraceId,
            InspectorTab = defaultTab,
            ReplayCursor = null,
            ReplayStepCount = Math.Max(0, stepCount),
            Transcript = null,
            SelectedSourceFile = null,
        };
    }

    [IgnoreExplore]
    public CreatorWorkspaceState OpenInput(string? rawInput)
    {
        var input = rawInput?.Trim() ?? string.Empty;
        if (input.StartsWith("trace/", StringComparison.Ordinal)
            && input["trace/".Length..].Trim() is { Length: > 0 } traceId)
        {
            return this with
            {
                Selected = new Selection(Selection.Pulse, traceId),
                TraceId = traceId,
                InspectorTab = "signals",
                ReplayCursor = null,
            };
        }

        if (input.StartsWith("node:", StringComparison.Ordinal) && Selection.Parse(input) is { } node)
        {
            return this with
            {
                Selected = node,
                ReplayCursor = null,
            };
        }

        if (input.Length > 0 && Selection.Parse(input) is { } selected)
        {
            return this with
            {
                Selected = selected,
                ReplayCursor = null,
            };
        }

        return this;
    }

    [IgnoreExplore]
    public CreatorWorkspaceState ApplyArrival(StudioNavArgs? args)
    {
        if (args is null)
        {
            return this;
        }

        var next = this;
        if (!string.IsNullOrWhiteSpace(args.TargetId))
        {
            next = next.OpenInput(args.TargetId);
        }

        if (!string.IsNullOrWhiteSpace(args.FlowId))
        {
            next = next.SelectRun(args.FlowId!);
        }

        var modeCandidate = args.Mode ?? args.Lens;
        if (!string.IsNullOrWhiteSpace(modeCandidate))
        {
            if (TryNormalizeCanvasMode(modeCandidate) is { } canvasMode)
            {
                next = next.SetCanvasMode(canvasMode);
            }
            else if (TryNormalizeInspectorTab(modeCandidate) is { } tab)
            {
                next = next with { InspectorTab = tab };
            }
        }

        if (!string.IsNullOrWhiteSpace(args.FilePath))
        {
            next = next.SetSourceFile(args.FilePath);
        }

        return next;
    }

    [IgnoreExplore]
    public CreatorWorkspaceState SetCanvasMode(string? mode)
    {
        var normalized = TryNormalizeCanvasMode(mode) ?? "brain";
        return this with
        {
            CanvasMode = normalized,
            ShowStateGraph = normalized == "state",
        };
    }

    [IgnoreExplore]
    public CreatorWorkspaceState SetPreviewViewport(string? viewport)
    {
        var normalized = viewport?.Trim().ToLowerInvariant() switch
        {
            "720" => "720",
            "390" => "390",
            _ => "fill",
        };
        return this with { PreviewViewport = normalized };
    }

    [IgnoreExplore]
    public CreatorWorkspaceState SetSourceFile(string? fileName)
        => this with
        {
            SelectedSourceFile = string.IsNullOrWhiteSpace(fileName) ? null : fileName.Trim(),
            InspectorTab = "source",
        };

    [IgnoreExplore]
    public CreatorWorkspaceState StepReplay(string? cursorArg, int stepCount)
    {
        var normalizedCount = Math.Max(0, stepCount);
        var nextCursor = normalizedCount > 0
            ? ReplayScript.Move(ReplayCursor, cursorArg, normalizedCount)
            : null;
        return this with
        {
            ReplayCursor = nextCursor,
            ReplayStepCount = normalizedCount,
            Status = ReplayScript.FormatCursorReadout(nextCursor, normalizedCount),
        };
    }

    public static string? TryNormalizeInspectorTab(string? tab) => tab?.Trim().ToLowerInvariant() switch
    {
        "state" or "properties" => "state",
        "source" or "code" => "source",
        "signals" or "trace" or "events" => "signals",
        "state-graph" or "stategraph" or "graph" => "state-graph",
        "scenarios" or "scenario" => "scenarios",
        _ => null,
    };

    public static string? TryNormalizeCanvasMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "brain" or "topology" or "flow" => "brain",
        "state" or "state-graph" or "accordant" => "state",
        "live" or "preview" => "live",
        _ => null,
    };

    public static string FormatInspectorTabTitle(string tab) => tab switch
    {
        "source" => "Source",
        "signals" => "Signals",
        "state-graph" => "State Graph",
        "scenarios" => "Scenarios",
        _ => "State",
    };
}

// A selection of the graph: a node (its id; a ghost is draft:<alias>; a catalog synapse is synapse:<id>), an edge (its full key) or a pulse (its trace id).
public sealed record Selection(string Kind, string Id)
{
    public const string Node = "node", Edge = "edge", Pulse = "pulse";
    public const string SynapsePrefix = "synapse:";

    public static Selection? Parse(string? key) => key switch
    {
        null or "" => null,
        _ when key.StartsWith("node:", StringComparison.Ordinal) => new(Node, key["node:".Length..]),
        _ when key.StartsWith("edge:", StringComparison.Ordinal) => new(Edge, key),
        _ when key.StartsWith("pulse:", StringComparison.Ordinal) => key.Length > "pulse:".Length ? new(Pulse, key["pulse:".Length..]) : null,
        _ => new(Node, key),
    };

    public string GraphKey => Kind switch
    {
        Node when Id.StartsWith(SynapsePrefix, StringComparison.Ordinal) => Id,
        Node => BrainGraph.NodeKey(Id),
        Pulse => BrainGraph.PulseKey(Id),
        _ => Id,
    };
}

// What the inspector read for the selection (Render cannot await). It is stored with Creator's state, so a reactivated Creator draws
// the same inspector without reading again. Hops is the replay timeline: the selection's own trace, or the one kept from the last
// inspection that had one.
public sealed record Inspection(string Kind, string Id)
{
    public static readonly Inspection None = new("", "");

    public string? Alias { get; init; }
    public string? State { get; init; }
    public string? View { get; init; }
    public InspectedFile[] Files { get; init; } = [];
    public TraceEntry[] Signals { get; init; } = [];
    public TraceHop[] Hops { get; init; } = [];
    public string? Notice { get; init; }
    public IntoChat.OS.Modules.Apps.Synapses.SynapseDefinition? Synapse { get; init; }
    public DigitalBrain.Units.DeployedSource? DeployedUnit { get; init; }
    public IntoChat.OS.Modules.AI.Agents.Context.UiStateGraphSnapshot? IntrospectedGraph { get; init; }
    public double? ScenarioPassRate { get; init; }
    public string? FailureSummary { get; init; }

    public string? FileText(string path) => Array.Find(Files, file => string.Equals(file.Path, path, StringComparison.Ordinal))?.Text;

    public IReadOnlyDictionary<string, string> FileMap()
        => Files.ToDictionary(static file => file.Path, static file => file.Text, StringComparer.Ordinal);

    public static InspectedFile[] FilesOf(IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return [.. files.OrderBy(static file => file.Key, StringComparer.Ordinal).Select(static file => new InspectedFile(file.Key, file.Value))];
    }
}

// One file of the inspected unit or app: its path and its text.
public sealed record InspectedFile(string Path, string Text);

// One journaled signal of a trace, folded: Depth is its causation chain length to a root hop.
public sealed record TraceHop(
    long Sequence,
    string Publisher,
    string Alias,
    string Address,
    string? Cause,
    int Depth,
    DateTimeOffset At,
    string Payload,
    string? FlowId);
