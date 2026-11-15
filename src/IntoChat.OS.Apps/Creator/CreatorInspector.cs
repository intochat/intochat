using System.Globalization;
using DigitalBrain.Flows;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Creator;

/// <summary>
/// Right pane: Inspector for selected node/edge/pulse, Accordant state-space summary, change-run prompts, and interactive approve/reject actions.
/// </summary>
public static class CreatorInspector
{
    private const string App = CreatorView.App;

    public static Node Render(CreatorState state, Inspection inspection, FlowView[] flows, Approval[] approvals)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(inspection);

        List<Node> children =
        [
            Button("Clear selection", "x")
                .Key(BrainGraph.SelectKey)
                .Small()
                .Emits(AppSignals.Emit(App, "select"))
                .ToNode(),
        ];

        if (state.Selected is null || inspection.Kind.Length == 0)
        {
            children.Add(Text("Select a node, an edge or a pulse of the graph."));

            if (approvals.Length > 0)
            {
                children.Add(Divider("PENDING APPROVALS"));
                foreach (var approval in approvals)
                {
                    children.Add(StatusPill(StatusKind.NeedsYou, "Needs Approval"));
                    children.Add(new ApprovalCardNode($"approval:{approval.PlanId}", approval.PlanId, approval.Title, approval.Detail, []));
                    children.Add(HStack(6)[
                        PrimaryButton("Approve & Promote", Icons.Check)
                            .Key($"approve:{approval.PlanId}")
                            .Small()
                            .Emits(AppSignals.Emit(App, "approve", approval.PlanId)),
                        Button("Reject & Abort", "x")
                            .Key($"reject:{approval.PlanId}")
                            .Small()
                            .Emits(AppSignals.Emit(App, "reject", approval.PlanId))
                    ]);
                }
            }

            return VStack([.. children]);
        }

        if (inspection.Notice is { } notice)
        {
            children.Add(Banner("Note", notice));
        }

        children.AddRange(inspection.Kind switch
        {
            Selection.Edge => Edge(inspection),
            Selection.Pulse => Pulse(state, inspection),
            _ => Node(state, inspection, flows, approvals),
        });

        return VStack([.. children]);
    }

    private static IEnumerable<Node> Node(CreatorState state, Inspection inspection, FlowView[] flows, Approval[] approvals)
    {
        if (inspection.Alias is not { } alias)
        {
            yield return Card(inspection.Id);
            if (inspection.Id.Split("/apps/") is [_, var app] && app.Length > 0)
            {
                yield return new Button("open-app", $"Open {app}", Intent: AppSignals.OpenApp(app)) { Small = true, Icon = "external-link" };
            }

            yield break;
        }

        var ghost = inspection.Id.StartsWith("draft:", StringComparison.Ordinal);
        yield return Card(alias, ghost ? "Under construction: drawn from its build flow until it is installed." : inspection.Id)[
            StatusPill(ghost ? StatusKind.Working : StatusKind.Done, ghost ? "Under Construction" : "Live Unit")
        ];
        if (!ghost)
        {
            yield return Card("State", inspection.State ?? "No state saved yet.");
            yield return Card("View", inspection.View ?? "No view published yet.");
        }

        foreach (var name in new[] { "spec.md" }.Concat(inspection.Files.Keys.Where(k => k.EndsWith(".cs", StringComparison.Ordinal)).Order(StringComparer.Ordinal)).Append("scenarios.json"))
        {
            if (inspection.Files.TryGetValue(name, out var text))
            {
                yield return Card(name, text);
            }
        }

        if (inspection.Files.Count == 0)
        {
            yield return MutedText("No source kept for this unit.");
        }

        // Needs and consent: build flow and approvals
        var flow = flows.Where(f => f.Subject == alias).OrderByDescending(f => f.Started).FirstOrDefault();
        if (flow is not null)
        {
            yield return new Button("open-flow", $"Build flow: {flow.Status.ToString().ToLowerInvariant()}", Intent: AppSignals.OpenApp("tasks", flow.FlowId)) { Small = true, Icon = "tasks" };
            if (CreatorView.Consent(flow, approvals) is { } consent)
            {
                yield return new ApprovalCardNode("consent", consent.PlanId, consent.Title, consent.Detail, []);
                yield return MutedText("Approve or deny in the inbox.");
            }
        }

        foreach (var staged in approvals.Where(a => a.Kind == ApprovalKind.SynapseStaged && a.Subject == alias && a.PlanId != flow?.ConfirmationPlanId))
        {
            yield return new ApprovalCardNode($"needs:{staged.PlanId}", staged.PlanId, staged.Title, staged.Detail, []);
        }

        // Prompt / refine change input
        yield return TextField(CreatorView.FieldKey(CreatorView.ChangeField), $"How should {alias} change?")
            .Key(CreatorView.ChangeField);
        yield return HStack(Space.Sm)[
            PrimaryButton("Change it", Icons.Sparkles)
                .Key("change-it")
                .Small()
                .Emits(AppSignals.Emit(App, "change", alias, state.ChangeDraft)),
            Button("Run scenario", "play")
                .Key("run-scenario")
                .Small()
                .Emits(AppSignals.Emit(App, "scenario", alias)),
            Button("Share as App-Post", "share-2")
                .Key("share-unit")
                .Small()
                .Emits(AppSignals.Emit(App, "share", alias))
        ];

        // Interactive Approve / Reject actions for any pending approval
        var relevantApprovals = approvals.Where(a =>
            a.Subject == alias ||
            a.Subject == "creator" ||
            a.Detail.Contains("creator", StringComparison.OrdinalIgnoreCase) ||
            a.PlanId == flow?.ConfirmationPlanId).ToArray();

        if (relevantApprovals.Length > 0)
        {
            yield return Divider("PENDING APPROVAL GATE");
            foreach (var apprv in relevantApprovals)
            {
                yield return StatusPill(StatusKind.NeedsYou, "Needs Your Approval");
                yield return new ApprovalCardNode($"gate:{apprv.PlanId}", apprv.PlanId, apprv.Title, apprv.Detail, []);
                yield return HStack(6)[
                    PrimaryButton("Approve & Promote", Icons.Check)
                        .Key($"approve:{apprv.PlanId}")
                        .Small()
                        .Emits(AppSignals.Emit(App, "approve", apprv.PlanId)),
                    Button("Reject & Abort", "x")
                        .Key($"reject:{apprv.PlanId}")
                        .Small()
                        .Emits(AppSignals.Emit(App, "reject", apprv.PlanId))
                ];
            }
        }

        if (state.Transcript is { } transcript)
        {
            yield return Card("Scenario transcript", transcript);
        }
    }

    private static IEnumerable<Node> Edge(Inspection inspection)
    {
        var parts = BrainGraph.ParseEdge(inspection.Id);
        yield return Card(inspection.Alias ?? inspection.Id, parts is { } p ? $"{p.From} → {p.To}" : inspection.Id);
        yield return Table(
            $"Last {inspection.Signals.Count} signal(s)",
            ["#", "At", "Publisher", "Trace"],
            [.. inspection.Signals.Select(s => (IReadOnlyList<string>)[
                s.Sequence.ToString(CultureInfo.InvariantCulture),
                s.At.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                s.Publisher,
                s.TraceId ?? "",
            ])]);
        foreach (var traceId in inspection.Signals.Select(s => s.TraceId).OfType<string>().Distinct(StringComparer.Ordinal).TakeLast(5))
        {
            yield return Button($"Trace {traceId}", "activity")
                .Key($"open-trace:{traceId}")
                .Small()
                .Emits(AppSignals.Emit(App, "trace", traceId))
                .ToNode();
        }
    }

    private static IEnumerable<Node> Pulse(CreatorState state, Inspection inspection)
    {
        var hops = inspection.Hops;
        if (hops.Count == 0)
        {
            yield break;
        }

        var first = hops.Min(h => h.At);
        var steps = ReplayScript.Steps(hops);
        var cursor = state.ReplayCursor is { } c ? Math.Clamp(c, 0, steps.Length - 1) : (long?)null;
        var current = cursor is { } at ? steps[at].Sequence : (long?)null;

        yield return Table(
            $"Trace {inspection.Id}",
            ["#", "Δt", "Publisher", "Signal", "Cause"],
            [.. hops.Select(h => (IReadOnlyList<string>)[
                (h.Sequence == current ? "▶ " : "") + h.Sequence.ToString(CultureInfo.InvariantCulture),
                string.Create(CultureInfo.InvariantCulture, $"+{(h.At - first).TotalMilliseconds:0}ms"),
                TraceFold.Indent(h.Depth) + h.Publisher,
                h.Alias,
                h.Cause ?? "",
            ])]);

        yield return new Progress(
            "Replay",
            cursor is { } position ? (steps.Length == 1 ? 1 : (double)position / (steps.Length - 1)) : 0,
            cursor is { } now ? $"step {now + 1} of {steps.Length}: {steps[now].Alias}" : $"live; {steps.Length} step(s)");

        yield return HStack(Space.Xs)[
            Button("First", "chevrons-left").Key("replay-first").Small().Emits(AppSignals.Emit(App, "replay", "first")),
            Button("Prev", "chevron-left").Key("replay-prev").Small().Emits(AppSignals.Emit(App, "replay", "prev")),
            Button("Next", "chevron-right").Key("replay-next").Small().Emits(AppSignals.Emit(App, "replay", "next")),
            Button("Last", "chevrons-right").Key("replay-last").Small().Emits(AppSignals.Emit(App, "replay", "last")),
            Button("Live", "radio").Key("replay-live").Small().Emits(AppSignals.Emit(App, "replay", "stop"))
        ];

        if (cursor is { } step && ReplayScript.Diff(steps, step) is { } diff)
        {
            yield return Table($"State change at step {step + 1}", ["Property", "Before", "After"], [.. diff.Select(row => (IReadOnlyList<string>)row)]);
        }

        foreach (var flowId in hops.Select(h => h.FlowId).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            yield return new Button($"open-flow:{flowId}", "Open the flow", Intent: AppSignals.OpenApp("tasks", flowId)) { Small = true, Icon = "tasks" };
        }
    }
}
