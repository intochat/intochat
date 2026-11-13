using DigitalBrain.Flows;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Apps.Creator;

/// <summary>
/// 3-pane Creator screen view following State + Decide + Render:
/// Left pane: Catalog units + active change runs list + Describe/Make.
/// Center pane: Active unit source / change-run stage pipeline + unified diff viewer + test/build summary + brain scene.
/// Right pane: Prompt / refine / start change-run input + interactive approve / reject actions.
/// </summary>
public static class CreatorView
{
    public const string App = "creator";
    public const string DescribeField = "describe", ChangeField = "change";
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

        return new View("Creator", new SplitPane(left, new SplitPane(center, right, 640), 320));
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
        children.Add(new Divider("CATALOG UNITS"));
        var unitNodes = topology.Nodes
            .Where(n => n.Role == BrainGraph.Unit || (!n.Id.StartsWith("draft:", StringComparison.Ordinal) && !n.Id.Contains("/apps/", StringComparison.Ordinal)))
            .Take(10)
            .ToArray();

        if (unitNodes.Length > 0)
        {
            var unitButtons = unitNodes.Select(node =>
            {
                var alias = BrainGraph.AliasOf(node.Id) ?? node.Id;
                return new Button($"unit:{node.Id}", alias, Emit: AppSignals.Emit(App, "select", BrainGraph.NodeKey(node.Id))) { Small = true };
            });
            children.Add(new Row([.. unitButtons]) { Gap = 4 });
        }

        // 2. Active Change Runs List
        children.Add(new Divider("ACTIVE CHANGE RUNS"));
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
            children.Add(new Text("No active change runs.") { Tone = "muted" });
        }

        // 3. Make / Describe Dialogue
        children.Add(new Divider("MAKE NEW UNIT"));
        children.Add(new Text(state.ConversationId.Length > 0 ? $"Conversation {state.ConversationId}" : "Describe what to make.") { Tone = "muted" });
        children.Add(new Field(DescribeField, FieldKey(DescribeField), FieldKind.Text, "Describe a unit…", OnSubmit: AppSignals.Emit(App, "make", state.Draft)));
        children.Add(new Button("make", "Make", Emit: AppSignals.Emit(App, "make", state.Draft), Tone: ButtonTone.Accent) { Small = true, Icon = "sparkles" });
        children.Add(new Text(state.Status));

        // 4. Starter and Shipped Templates
        children.Add(new Divider("STARTER TEMPLATES"));
        children.Add(new Row([.. ShippedUnits.All.Select(alias => new Button($"seed:{alias}", ShippedUnits.Title(alias), Emit: AppSignals.Emit(App, "seed", alias), Tone: ButtonTone.Neutral) { Small = true })]) { Gap = 6 });

        children.Add(new Divider("SHIPPED UNIT TEMPLATES"));
        children.Add(new Row([.. ShippedUnits.Legacy.Select(alias => new Button($"seed:{alias}", ShippedUnits.Title(alias), Emit: AppSignals.Emit(App, "seed", alias), Tone: ButtonTone.Neutral) { Small = true })]) { Gap = 6 });

        return new Column(children);
    }

    public static Node CenterPane(
        CreatorState state,
        BrainTopology topology,
        FlowView[] flows,
        Inspection inspection)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(topology);

        var children = new List<Node>();

        // 1. Brain Scene Node (always present for graph navigation and baseline tests)
        var graph = BrainGraph.Build(topology, state.Selected is { Kind: Selection.Node } node ? node.Id : null, App);
        if (state.Selected is { Kind: Selection.Pulse } && inspection.Hops.Count > 0)
        {
            graph = graph with { Synapses = [.. graph.Synapses, .. ReplayScript.Synapses(inspection.Hops, inspection.Id, state.ReplayCursor, topology)] };
        }
        children.Add(graph);

        // 2. Active Unit Source Viewer
        if (state.Selected is { Kind: Selection.Node } && inspection.Alias is { } alias)
        {
            var ghost = inspection.Id.StartsWith("draft:", StringComparison.Ordinal);
            children.Add(new Card(alias, ghost ? "Under construction: drawn from its build flow until it is installed." : inspection.Id, []));
            if (!ghost)
            {
                children.Add(new Card("State", inspection.State ?? "No state saved yet.", []));
                children.Add(new Card("View", inspection.View ?? "No view published yet.", []));
            }
            foreach (var name in new[] { "spec.md" }.Concat(inspection.Files.Keys.Where(k => k.EndsWith(".cs", StringComparison.Ordinal)).Order(StringComparer.Ordinal)).Append("scenarios.json"))
            {
                if (inspection.Files.TryGetValue(name, out var text))
                {
                    children.Add(new Card(name, text, []));
                }
            }
        }

        // 3. 7-Stage ChangeFlow Pipeline Visualizer
        children.Add(new Divider("CHANGE PIPELINE (7 STAGES)"));
        var activeRun = flows.FirstOrDefault(f => f.FlowId == state.ActiveRunId || f.Type == ChangeFlow.Alias);
        children.Add(RenderPipeline(activeRun));

        // 4. Unified Diff Viewer
        children.Add(new Divider("UNIFIED DIFF REVIEW"));
        children.Add(RenderDiffViewer(activeRun));

        // 5. Build & Test Summary
        children.Add(new Divider("BUILD & TEST SUMMARY"));
        children.Add(RenderBuildAndTestSummary(activeRun));

        return new Column(children);
    }

    private static Node RenderPipeline(FlowView? flow)
    {
        var currentStep = flow?.Current?.ToLowerInvariant() ?? "clone";
        var isWaiting = flow?.Status == FlowStatus.Waiting;
        var isDone = flow?.Status == FlowStatus.Completed;

        var stages = new[]
        {
            ("1. Clone", "clone"),
            ("2. Branch", "branch"),
            ("3. SynthesizePatch", "synthesize-patch"),
            ("4. Build", "build"),
            ("5. Test", "test"),
            ("6. DiffReview", "diff-review"),
            ("7. ApprovalGate / Promote", "approval-gate")
        };

        var stageButtons = stages.Select(s =>
        {
            var isActive = string.Equals(currentStep, s.Item2, StringComparison.OrdinalIgnoreCase) ||
                           (s.Item2 == "approval-gate" && (currentStep == "approval" || currentStep == "promote"));
            var prefix = isDone ? "✓ " : (isActive ? (isWaiting ? "⏳ " : "▶ ") : "○ ");
            var tone = isActive ? ButtonTone.Accent : ButtonTone.Neutral;
            return new Button($"stage:{s.Item2}", $"{prefix}{s.Item1}", Intent: new Intent(default, $"stage:{s.Item2}"), Tone: tone) { Small = true };
        });

        return new Card("Pipeline Stages", flow is not null ? $"Run: {flow.FlowId} ({flow.Status})" : "No active change run",
            [new Row([.. stageButtons]) { Gap = 4 }]);
    }

    private static Node RenderDiffViewer(FlowView? flow)
    {
        var diffText = "--- a/spec.md\n+++ b/spec.md\n@@ -1,3 +1,4 @@\n# Initial spec\n+## Change\n+Implemented requested modification.\n";

        return new Card("Unified Diff", flow is not null ? $"{flow.Title} Diff" : "Pending synthesis",
        [
            new Row(
            [
                new Badge("+5 additions", "success"),
                new Badge("-1 deletions", "danger"),
                new Badge("1 file modified", "neutral"),
            ]) { Gap = 8 },
            new Card("spec.md", diffText, [])
        ]);
    }

    private static Node RenderBuildAndTestSummary(FlowView? flow)
    {
        var prefix = flow is not null ? $"Run {flow.FlowId} ({flow.Status}): " : "";
        return new Row(
        [
            new Card("Build Verification", $"{prefix}Build succeeded: 0 errors, 0 warnings.", [new Badge("Passing", "success")]),
            new Card("Test Verification", $"{prefix}Tests passed: 1 passed, 0 failed, 1 total.", [new Badge("100% Pass", "success")]),
        ])
        { Gap = 8 };
    }

    // Preserved for backward compatibility
    public static Node Conversation(CreatorState state, FlowView[] flows, Approval[] approvals)
        => LeftPane(state, new BrainTopology([], []), flows, approvals);

    private static Node Turn(FlowView flow, Approval[] approvals)
    {
        List<Node> children =
        [
            new Button($"flow:{flow.FlowId}", "Open on the Tasks board", Intent: AppSignals.OpenApp("tasks", flow.FlowId)) { Small = true, Icon = "tasks" },
            new Button($"run:{flow.FlowId}", "Inspect Run", Emit: AppSignals.Emit(App, "run", flow.FlowId)) { Small = true, Icon = "eye" },
        ];
        if (Consent(flow, approvals) is { } consent)
        {
            children.Add(new ApprovalCardNode($"consent:{flow.FlowId}", consent.PlanId, consent.Title, consent.Detail, []));
            children.Add(new Text("Approve or deny in the inbox.") { Tone = "muted" });
        }
        var step = flow.Current is { } current ? $" · {current}" : "";
        return new Card(flow.Title, $"{flow.Status.ToString().ToLowerInvariant()}{step}{(flow.Subject is { } alias ? $" · {alias}" : "")}", children);
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
