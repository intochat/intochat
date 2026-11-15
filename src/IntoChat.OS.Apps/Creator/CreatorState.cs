using DigitalBrain.Rules;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.Creator;

// Selected is what the inspector shows; TraceId the trace last opened (a pulse); ReplayCursor the replay scrubber's step (an index into its signals in sequence order, null when live);
// ConversationId the Ino conversation agent the dialogue belongs to.
public sealed record CreatorState(Selection? Selected, string? TraceId, long? ReplayCursor, string ConversationId)
{
    public CreatorState() : this(null, null, null, "") { }

    public string Status { get; init; } = "Ready";

    // What the describe and change fields hold, folded from their text fields' changes.
    public string Draft { get; init; } = "";

    public string ChangeDraft { get; init; } = "";

    // The last scenario run's transcript.
    public string? Transcript { get; init; }

    // Active change run being inspected or displayed in center/right panes.
    public string? ActiveRunId { get; init; }

    public string? SelectedStage { get; init; }

    // Whether the Accordant Aggregate State-Space Graph inspector overlay/card is expanded.
    public bool ShowStateGraph { get; init; }

    // Studio canvas mode: "brain" | "live" | "state" | "source".
    public string CanvasMode { get; init; } = "brain";

    // Studio live preview viewport width: "fill" | "720" | "390".
    public string PreviewViewport { get; init; } = "fill";

    // ── Domain Rules & Invariants ────────────────────────────────────────────

    [Invariant]
    public bool NonNegativeReplayCursor => ReplayCursor is null or >= 0;

    [Invariant]
    public bool ValidCanvasMode => CanvasMode is "brain" or "live" or "state" or "source";

    [Invariant]
    public bool ValidPreviewViewport => PreviewViewport is "fill" or "720" or "390";

    public Rule EmptyDraft => Decision.Rule("empty_draft", string.IsNullOrWhiteSpace(Draft), "Describe what unit you want to make first.");

    public Rule EmptyChangeDraft => Decision.Rule("empty_change_draft", string.IsNullOrWhiteSpace(ChangeDraft), "Describe how this unit should change first.");

    public Rule RequiresSelectedNode => Decision.Rule("requires_selected_node", Selected is not { Kind: Selection.Node }, "Select a unit node in the graph first.");

    // ── Rich DDD Aggregate Transitions ───────────────────────────────────────

    public CreatorState SelectNode([Sample("local/ctr/default")] string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        return this with
        {
            Selected = Selection.Parse(nodeId),
            Transcript = null,
            ReplayCursor = null,
        };
    }

    [IgnoreExplore]
    public CreatorState SelectSelection(Selection? selection) => this with
    {
        Selected = selection,
        TraceId = selection?.Kind == Selection.Pulse ? selection.Id : TraceId,
        ReplayCursor = null,
        Transcript = null,
    };

    public CreatorState OpenInput([Sample("node:local/ctr/default", "trace/t-1")] string? rawInput)
    {
        var input = rawInput?.Trim() ?? string.Empty;
        if (input.StartsWith("trace/", StringComparison.Ordinal) && input["trace/".Length..].Trim() is { Length: > 0 } traceId)
        {
            return this with { Selected = new Selection(Selection.Pulse, traceId), TraceId = traceId, ReplayCursor = null };
        }

        if (input.StartsWith("node:", StringComparison.Ordinal) && Selection.Parse(input) is { } node)
        {
            return this with { Selected = node };
        }

        return this;
    }

    public CreatorState SelectRun([Sample("run-1")] string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return this with
        {
            ActiveRunId = runId,
            Status = $"Inspecting change run {runId}",
        };
    }

    public CreatorState SetDraft([Sample("watch elonmusk on X")] string draft)
        => this with { Draft = draft ?? string.Empty };

    public CreatorState SetChangeDraft([Sample("count by two")] string changeDraft)
        => this with { ChangeDraft = changeDraft ?? string.Empty };

    [IgnoreExplore]
    public CreatorState SetStatus(string status)
        => this with { Status = status ?? string.Empty };

    [IgnoreExplore]
    public CreatorState SetTranscript(string? transcript)
        => this with { Transcript = transcript };

    public CreatorState SetStage([Sample("clone", "diff-review")] string? stage)
        => this with { SelectedStage = stage };

    public CreatorState ToggleStateGraph()
        => this with
        {
            ShowStateGraph = !ShowStateGraph,
            CanvasMode = !ShowStateGraph ? "state" : "brain",
        };

    [IgnoreExplore]
    public CreatorState SetCanvasMode(string? mode)
    {
        var normalized = mode?.Trim().ToLowerInvariant() switch
        {
            "live" => "live",
            "state" => "state",
            "source" => "source",
            _ => "brain",
        };
        return this with
        {
            CanvasMode = normalized,
            ShowStateGraph = normalized == "state",
        };
    }

    [IgnoreExplore]
    public CreatorState SetPreviewViewport(string? viewport)
    {
        var normalized = viewport?.Trim().ToLowerInvariant() switch
        {
            "720" => "720",
            "390" => "390",
            _ => "fill",
        };
        return this with { PreviewViewport = normalized };
    }

    public CreatorState StepReplay([Sample("next", "stop")] string? cursorArg, [Sample(4)] int hopCount)
        => this with
        {
            ReplayCursor = Selected is { Kind: Selection.Pulse }
                ? ReplayScript.Move(ReplayCursor, cursorArg, hopCount)
                : null,
        };

    public CreatorState ClearSelection() => this with
    {
        Selected = null,
        ActiveRunId = null,
        Transcript = null,
        ReplayCursor = null,
    };

    public Decision<CreatorState> ValidateMake([Sample("", "watch elonmusk on X")] string? explicitGoal = null)
    {
        var goal = string.IsNullOrWhiteSpace(explicitGoal) ? Draft.Trim() : explicitGoal.Trim();
        return Decision.Check("creator.make", Decision.Rule("empty_draft", goal.Length == 0, "Describe what to make."))
            ?? Decision.Accept(this with { Draft = string.Empty, Status = $"Building {goal}" });
    }

    public Decision<CreatorState> ValidateChange([Sample("", "count by two")] string? explicitRequest = null)
    {
        var wanted = string.IsNullOrWhiteSpace(explicitRequest) ? ChangeDraft.Trim() : explicitRequest.Trim();
        return Decision.Check(
                "creator.change",
                RequiresSelectedNode,
                Decision.Rule("empty_change_draft", wanted.Length == 0, "Describe how the selected unit should change."))
            ?? Decision.Accept(this with { ChangeDraft = string.Empty });
    }
}

// A selection of the graph: a node (its id; a ghost is draft:<alias>), an edge (its full key) or a pulse (its trace id).
public sealed record Selection(string Kind, string Id)
{
    public const string Node = "node", Edge = "edge", Pulse = "pulse";

    // A graph key back into a selection; a bare id is a node. Null clears.
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
        Node => BrainGraph.NodeKey(Id),
        Pulse => BrainGraph.PulseKey(Id),
        _ => Id,
    };
}

// What the inspector read for the selection in the last turn (Render cannot await).
public sealed record Inspection(string Kind, string Id)
{
    public static readonly Inspection None = new("", "");

    public string? Alias { get; init; }
    public string? State { get; init; }
    public string? View { get; init; }
    public IReadOnlyDictionary<string, string> Files { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<TraceEntry> Signals { get; init; } = [];
    public IReadOnlyList<TraceHop> Hops { get; init; } = [];
    public string? Notice { get; init; }
}

// One journaled signal of a trace, folded: Depth is its causation chain length to a root hop.
public sealed record TraceHop(long Sequence, string Publisher, string Alias, string Address, string? Cause, int Depth, DateTimeOffset At, string Payload, string? FlowId);
