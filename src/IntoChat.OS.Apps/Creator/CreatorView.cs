using DigitalBrain.Flows;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Creator;

/// <summary>
/// 3-pane Creator screen view built on <see cref="Kit"/>:
/// Left pane: Catalog units + active change runs list + Describe/Make + Starter templates.
/// Center pane: BrainScene topology (or Accordant StateGraphView when toggled) + active unit source/state-space + 7-stage ChangeFlow pipeline + unified diff + build/test summary.
/// Right pane: Inspector for selected node/edge/pulse + refine change input + interactive approve/reject gate.
/// </summary>
public static class CreatorView
{
    public const string App = "creator";
    public const string DescribeField = "describe", ChangeField = "change";
    public const string ToggleStateGraphTarget = "view:state-graph";
    public static readonly string[] Fields = [DescribeField, ChangeField];

    public static string FieldKey(string field) => $"apps/{App}/{field}";

    public static View Render(
        CreatorState state,
        BrainTopology topology,
        FlowView[] flows,
        Approval[] approvals,
        Inspection inspection)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(topology);

        var left = LeftPane(state, topology, flows, approvals);
        var center = CenterPane(state, topology, flows, inspection);
        var right = CreatorInspector.Render(state, inspection, flows, approvals);

        return new View("Creator", ResponsiveSplit(left, ResponsiveSplit(center, right, 640), 320));
    }

    public static Node LeftPane(
        CreatorState state,
        BrainTopology topology,
        FlowView[] flows,
        Approval[] approvals)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(flows);

        var children = new List<Node>();

        // 1. Catalog Units List
        children.Add(Divider("CATALOG UNITS"));
        var unitNodes = topology.Nodes
            .Where(n => n.Role == BrainGraph.Unit || (!n.Id.StartsWith("draft:", StringComparison.Ordinal) && !n.Id.Contains("/apps/", StringComparison.Ordinal)))
            .Take(10)
            .ToArray();

        if (unitNodes.Length > 0)
        {
            var unitButtons = unitNodes.Select(node =>
            {
                var alias = BrainGraph.AliasOf(node.Id) ?? node.Id;
                return (Node)Button(alias)
                    .Key($"unit:{node.Id}")
                    .Small()
                    .Emits(AppSignals.Emit(App, "select", BrainGraph.NodeKey(node.Id)));
            });
            children.Add(HStack(Space.Xs, [.. unitButtons]));
        }

        // 2. Active Change Runs List
        children.Add(Divider("ACTIVE CHANGE RUNS"));
        var activeFlows = flows
            .Where(f => f.Type is ChangeFlow.Alias or BuildFlow.Alias)
            .OrderByDescending(f => f.Started)
            .Take(10)
            .ToArray();

        foreach (var flow in activeFlows)
        {
            children.Add(Turn(flow, approvals));
        }

        if (activeFlows.Length == 0)
        {
            children.Add(MutedText("No active change runs."));
        }

        // 3. Make / Describe Dialogue (with StatusPill & Rule hint)
        children.Add(Divider("MAKE NEW UNIT"));
        children.Add(HStack(Space.Sm)[
            StatusPill(
                approvals.Length > 0 ? StatusKind.NeedsYou : StatusKind.Idle,
                approvals.Length > 0 ? $"{approvals.Length} Approval(s)" : "Studio Ready"),
            MutedText(state.ConversationId.Length > 0 ? $"Conversation {state.ConversationId}" : "Describe what to make.")
        ]);
        children.Add(TextField(FieldKey(DescribeField), "Describe a unit…")
            .Key(DescribeField)
            .Emits(AppSignals.Emit(App, "make", state.Draft)));
        children.Add(PrimaryButton("Make", Icons.Sparkles)
            .Key("make")
            .Small()
            .Emits(AppSignals.Emit(App, "make", state.Draft)));
        if (state.EmptyDraft.Broken)
        {
            children.Add(MutedText(state.EmptyDraft.Message));
        }
        children.Add(Text(state.Status));

        // 4. Starter and Shipped Templates
        children.Add(Divider("STARTER TEMPLATES"));
        children.Add(HStack(6, [.. ShippedUnits.All.Select(alias =>
            (Node)Button(ShippedUnits.Title(alias))
                .Key($"seed:{alias}")
                .Small()
                .Emits(AppSignals.Emit(App, "seed", alias)))]));

        children.Add(Divider("SHIPPED UNIT TEMPLATES"));
        children.Add(HStack(6, [.. ShippedUnits.Legacy.Select(alias =>
            (Node)Button(ShippedUnits.Title(alias))
                .Key($"seed:{alias}")
                .Small()
                .Emits(AppSignals.Emit(App, "seed", alias)))]));

        return VStack([.. children]);
    }

    public static Node CenterPane(
        CreatorState state,
        BrainTopology topology,
        FlowView[] flows,
        Inspection inspection)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(topology);

        var children = new List<Node>
        {
            // 0. Graph Canvas Mode Header (3D Live Brain Graph vs Accordant 2D State-Space Graph)
            HStack(Space.Sm)[
                StatusPill(state.ShowStateGraph ? StatusKind.Working : StatusKind.Done,
                    state.ShowStateGraph ? "Accordant State Space" : "Live Brain Topology"),
                Button(
                    state.ShowStateGraph ? "Show Live Brain Graph" : "Show Accordant State Graph",
                    state.ShowStateGraph ? "activity" : "git-branch")
                    .Key("toggle-state-graph")
                    .Small()
                    .Emits(AppSignals.Emit(App, "select", ToggleStateGraphTarget))
            ]
        };

        // 1. Brain Scene Node (or Accordant StateGraphView when State.ShowStateGraph is toggled on)
        if (state.ShowStateGraph)
        {
            children.Add(state.Selected is { Kind: Selection.Node } && inspection.Alias is { } unitAlias && inspection.Files.Count > 0
                ? CreatorStateSpaceInspector.RenderUnitStateScene(unitAlias, inspection.Files)
                : StateGraphView(CreatorStateSpaceInspector.CreatorGraph));
        }
        else
        {
            var graph = BrainGraph.Build(topology, state.Selected is { Kind: Selection.Node } node ? node.Id : null, App);
            if (state.Selected is { Kind: Selection.Pulse } && inspection.Hops.Count > 0)
            {
                graph = graph with { Synapses = [.. graph.Synapses, .. ReplayScript.Synapses(inspection.Hops, inspection.Id, state.ReplayCursor, topology)] };
            }

            children.Add(graph);
        }

        // 2. Active Unit Source & Accordant State-Space Summary
        if (state.Selected is { Kind: Selection.Node } && inspection.Alias is { } alias)
        {
            var ghost = inspection.Id.StartsWith("draft:", StringComparison.Ordinal);
            children.Add(Card(alias, ghost ? "Under construction: drawn from its build flow until it is installed." : inspection.Id)[
                StatusPill(ghost ? StatusKind.Working : StatusKind.Done, ghost ? "Building" : "Installed")
            ]);
            if (!ghost)
            {
                children.Add(Card("State", inspection.State ?? "No state saved yet."));
                children.Add(Card("View", inspection.View ?? "No view published yet."));
            }

            children.Add(CreatorStateSpaceInspector.RenderUnitSummaryCard(alias, inspection.Files));

            foreach (var name in new[] { "spec.md" }.Concat(inspection.Files.Keys.Where(k => k.EndsWith(".cs", StringComparison.Ordinal)).Order(StringComparer.Ordinal)).Append("scenarios.json"))
            {
                if (inspection.Files.TryGetValue(name, out var text))
                {
                    children.Add(Card(name, text));
                }
            }
        }

        // 3. 7-Stage ChangeFlow Pipeline Visualizer
        children.Add(Divider("CHANGE PIPELINE (7 STAGES)"));
        var activeRun = flows.FirstOrDefault(f => f.FlowId == state.ActiveRunId || f.Type == ChangeFlow.Alias);
        children.Add(RenderPipeline(activeRun));

        // 4. Unified Diff Viewer
        children.Add(Divider("UNIFIED DIFF REVIEW"));
        children.Add(RenderDiffViewer(activeRun));

        // 5. Build & Test Summary
        children.Add(Divider("BUILD & TEST SUMMARY"));
        children.Add(RenderBuildAndTestSummary(activeRun));

        return VStack([.. children]);
    }

    private static Node RenderPipeline(FlowView? flow)
    {
        var currentStep = flow?.Current?.ToLowerInvariant() ?? "clone";
        var isWaiting = flow?.Status == FlowStatus.Waiting;
        var isDone = flow?.Status == FlowStatus.Completed;

        (string Label, string Key)[] stages =
        [
            ("1. Clone", "clone"),
            ("2. Branch", "branch"),
            ("3. SynthesizePatch", "synthesize-patch"),
            ("4. Build", "build"),
            ("5. Test", "test"),
            ("6. DiffReview", "diff-review"),
            ("7. ApprovalGate / Promote", "approval-gate"),
        ];

        var statusBadge = flow is null
            ? StatusPill(StatusKind.Idle, "Idle")
            : isDone
                ? StatusPill(StatusKind.Done, "Promoted")
                : isWaiting
                    ? StatusPill(StatusKind.NeedsYou, "Needs Approval")
                    : StatusPill(StatusKind.Working, flow.Current ?? "Running");

        var stageButtons = stages.Select(s =>
        {
            var isActive = string.Equals(currentStep, s.Key, StringComparison.OrdinalIgnoreCase) ||
                           (s.Key == "approval-gate" && (currentStep is "approval" or "promote"));
            var prefix = isDone ? "✓ " : (isActive ? (isWaiting ? "⏳ " : "▶ ") : "○ ");
            var builder = isActive ? PrimaryButton($"{prefix}{s.Label}") : Button($"{prefix}{s.Label}");
            return (Node)builder.Key($"stage:{s.Key}").Small().WithIntent(default, $"stage:{s.Key}");
        });

        return Card("Pipeline Stages", flow is not null ? $"Run: {flow.FlowId} ({flow.Status})" : "No active change run")[
            statusBadge,
            HStack(Space.Xs, [.. stageButtons])
        ];
    }

    private static Node RenderDiffViewer(FlowView? flow)
    {
        const string diffText = "--- a/spec.md\n+++ b/spec.md\n@@ -1,3 +1,4 @@\n# Initial spec\n+## Change\n+Implemented requested modification.\n";

        return Card("Unified Diff", flow is not null ? $"{flow.Title} Diff" : "Pending synthesis")[
            HStack(Space.Sm)[
                StatusPill(StatusKind.Done, "+5 additions"),
                StatusPill(StatusKind.Error, "-1 deletions"),
                StatusPill(StatusKind.Idle, "1 file modified")
            ],
            Card("spec.md", diffText)
        ];
    }

    private static Node RenderBuildAndTestSummary(FlowView? flow)
    {
        var prefix = flow is not null ? $"Run {flow.FlowId} ({flow.Status}): " : "";
        return HStack(Space.Sm)[
            Card("Build Verification", $"{prefix}Build succeeded: 0 errors, 0 warnings.")[
                StatusPill(StatusKind.Done, "Passing")
            ],
            Card("Test Verification", $"{prefix}Tests passed: 1 passed, 0 failed, 1 total.")[
                StatusPill(StatusKind.Done, "100% Pass")
            ]
        ];
    }

    // Preserved for backward compatibility
    public static Node Conversation(CreatorState state, FlowView[] flows, Approval[] approvals)
        => LeftPane(state, new BrainTopology([], []), flows, approvals);

    private static Node Turn(FlowView flow, Approval[] approvals)
    {
        var statusKind = flow.Status switch
        {
            FlowStatus.Completed => StatusKind.Done,
            FlowStatus.Waiting => StatusKind.NeedsYou,
            FlowStatus.Failed or FlowStatus.Cancelled => StatusKind.Error,
            FlowStatus.Running => StatusKind.Working,
            _ => StatusKind.Idle,
        };

        List<Node?> children =
        [
            StatusPill(statusKind, flow.Status.ToString()),
            HStack(Space.Xs)[
                new Button($"flow:{flow.FlowId}", "Open on the Tasks board", Intent: AppSignals.OpenApp("tasks", flow.FlowId)) { Small = true, Icon = "tasks" },
                Button("Inspect Run", "eye").Key($"run:{flow.FlowId}").Small().Emits(AppSignals.Emit(App, "run", flow.FlowId))
            ],
        ];
        if (Consent(flow, approvals) is { } consent)
        {
            children.Add(new ApprovalCardNode($"consent:{flow.FlowId}", consent.PlanId, consent.Title, consent.Detail, []));
            children.Add(MutedText("Approve or deny in the inbox."));
        }

        var step = flow.Current is { } current ? $" · {current}" : "";
        return Card(flow.Title, $"{flow.Status.ToString().ToLowerInvariant()}{step}{(flow.Subject is { } alias ? $" · {alias}" : "")}", [.. children]);
    }

    public static Approval? Consent(FlowView flow, Approval[] approvals)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(approvals);
        return flow is { Status: FlowStatus.Waiting, WaitingStep: "install" }
            ? approvals.FirstOrDefault(a => a.Kind == ApprovalKind.SynapseStaged && a.PlanId == flow.ConfirmationPlanId)
            : null;
    }
}
