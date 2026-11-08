using System.Globalization;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Apps.Creator;

// The right pane: what the selected node, edge or pulse is, read from the kernel in the turn that selected it.
public static class CreatorInspector
{
    private const string App = CreatorView.App;

    public static Node Render(CreatorState state, Inspection inspection, FlowView[] flows, Approval[] approvals)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(inspection);
        // The renderer selects an edge or a pulse through this button, with their key as its value; pressed, it clears.
        List<Node> children = [new Button(BrainGraph.SelectKey, "Clear selection", Emit: AppSignals.Emit(App, "select")) { Small = true, Icon = "x" }];
        if (state.Selected is null || inspection.Kind.Length == 0)
        {
            children.Add(new Text("Select a node, an edge or a pulse of the graph."));
            return new Column(children);
        }
        if (inspection.Notice is { } notice) { children.Add(new InfoBar("Note", notice, "neutral")); }
        children.AddRange(inspection.Kind switch
        {
            Selection.Edge => Edge(inspection),
            Selection.Pulse => Pulse(state, inspection),
            _ => Node(state, inspection, flows, approvals),
        });
        return new Column(children);
    }

    private static IEnumerable<Node> Node(CreatorState state, Inspection inspection, FlowView[] flows, Approval[] approvals)
    {
        if (inspection.Alias is not { } alias)
        {
            yield return new Card(inspection.Id, "", []);
            if (inspection.Id.Split("/apps/") is [_, var app] && app.Length > 0)
            {
                yield return new Button("open-app", $"Open {app}", Intent: AppSignals.OpenApp(app)) { Small = true, Icon = "external-link" };
            }
            yield break;
        }
        var ghost = inspection.Id.StartsWith("draft:", StringComparison.Ordinal);
        yield return new Card(alias, ghost ? "Under construction: drawn from its build flow until it is installed." : inspection.Id, []);
        if (!ghost)
        {
            yield return new Card("State", inspection.State ?? "No state saved yet.", []);
            yield return new Card("View", inspection.View ?? "No view published yet.", []);
        }
        foreach (var name in new[] { "spec.md" }.Concat(inspection.Files.Keys.Where(k => k.EndsWith(".cs", StringComparison.Ordinal)).Order(StringComparer.Ordinal)).Append("scenarios.json"))
        {
            if (inspection.Files.TryGetValue(name, out var text)) { yield return new Card(name, text, []); }
        }
        if (inspection.Files.Count == 0) { yield return new Text("No source kept for this unit.") { Tone = "muted" }; }

        // Needs and consent: the build flow of the alias and the SynapseStaged card its install waits on (answered in the inbox).
        var flow = flows.Where(f => f.Subject == alias).OrderByDescending(f => f.Started).FirstOrDefault();
        if (flow is not null)
        {
            yield return new Button("open-flow", $"Build flow: {flow.Status.ToString().ToLowerInvariant()}", Intent: AppSignals.OpenApp("tasks", flow.FlowId)) { Small = true, Icon = "tasks" };
            if (CreatorView.Consent(flow, approvals) is { } consent)
            {
                yield return new ApprovalCardNode("consent", consent.PlanId, consent.Title, consent.Detail, []);
                yield return new Text("Approve or deny in the inbox.") { Tone = "muted" };
            }
        }
        foreach (var staged in approvals.Where(a => a.Kind == ApprovalKind.SynapseStaged && a.Subject == alias && a.PlanId != flow?.ConfirmationPlanId))
        {
            yield return new ApprovalCardNode($"needs:{staged.PlanId}", staged.PlanId, staged.Title, staged.Detail, []);
        }

        yield return new Field(CreatorView.ChangeField, CreatorView.FieldKey(CreatorView.ChangeField), FieldKind.Text, $"How should {alias} change?");
        yield return new Row(
        [
            new Button("change-it", "Change it", Emit: AppSignals.Emit(App, "change", alias, state.ChangeDraft)) { Small = true, Icon = "sparkles" },
            new Button("run-scenario", "Run scenario", Emit: AppSignals.Emit(App, "scenario", alias)) { Small = true, Icon = "play" },
        ]);
        if (state.Transcript is { } transcript) { yield return new Card("Scenario transcript", transcript, []); }
    }

    private static IEnumerable<Node> Edge(Inspection inspection)
    {
        var parts = BrainGraph.ParseEdge(inspection.Id);
        yield return new Card(inspection.Alias ?? inspection.Id, parts is { } p ? $"{p.From} → {p.To}" : inspection.Id, []);
        yield return new DataTable($"Last {inspection.Signals.Count} signal(s)", ["#", "At", "Publisher", "Trace"],
            [.. inspection.Signals.Select(s => (IReadOnlyList<string>)[
                s.Sequence.ToString(CultureInfo.InvariantCulture),
                s.At.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                s.Publisher,
                s.TraceId ?? "",
            ])]);
        foreach (var traceId in inspection.Signals.Select(s => s.TraceId).OfType<string>().Distinct(StringComparer.Ordinal).TakeLast(5))
        {
            yield return new Button($"open-trace:{traceId}", $"Trace {traceId}", Emit: AppSignals.Emit(App, "trace", traceId)) { Small = true, Icon = "activity" };
        }
    }

    // The whole trace as a causation tree, and the replay scrubber (Task 23 replays the pulses in order).
    private static IEnumerable<Node> Pulse(CreatorState state, Inspection inspection)
    {
        var hops = inspection.Hops;
        if (hops.Count == 0) { yield break; }
        var first = hops.Min(h => h.At);
        yield return new DataTable($"Trace {inspection.Id}", ["#", "Δt", "Publisher", "Signal", "Cause"],
            [.. hops.Select(h => (IReadOnlyList<string>)[
                h.Sequence.ToString(CultureInfo.InvariantCulture),
                string.Create(CultureInfo.InvariantCulture, $"+{(h.At - first).TotalMilliseconds:0}ms"),
                TraceFold.Indent(h.Depth) + h.Publisher,
                h.Alias,
                h.Cause ?? "",
            ])]);
        var floor = hops.Min(h => h.Sequence);
        var last = hops.Max(h => h.Sequence);
        var at = state.ReplayCursor is { } cursor ? Math.Clamp(cursor, floor, last) : last;
        yield return new Progress("Replay", last == floor ? 1 : (double)(at - floor) / (last - floor), $"hop {at} of {floor}..{last}");
        foreach (var flowId in hops.Select(h => h.FlowId).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            yield return new Button($"open-flow:{flowId}", "Open the flow", Intent: AppSignals.OpenApp("tasks", flowId)) { Small = true, Icon = "tasks" };
        }
    }
}
