using DigitalBrain.Flows;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Apps.Creator;

// The Creator's three panes: the dialogue, the brain graph and the inspector.
public static class CreatorView
{
    public const string App = "creator";
    public const string DescribeField = "describe", ChangeField = "change";
    public static readonly string[] Fields = [DescribeField, ChangeField];

    // A field is the textfield neuron apps/creator/{field}; the app watches it.
    public static string FieldKey(string field) => $"apps/{App}/{field}";

    public static View Render(CreatorState state, BrainTopology topology, FlowView[] flows, Approval[] approvals, Inspection inspection)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(topology);
        var graph = BrainGraph.Build(topology, state.Selected is { Kind: Selection.Node } node ? node.Id : null, App);
        if (state.Selected is { Kind: Selection.Pulse } && inspection.Hops.Count > 0)
        {
            // The open trace's steps ride along as reserved synapses for the scene's replay.
            graph = graph with { Synapses = [.. graph.Synapses, .. ReplayScript.Synapses(inspection.Hops, inspection.Id, state.ReplayCursor, topology)] };
        }
        var right = CreatorInspector.Render(state, inspection, flows, approvals);
        return new View("Creator", new SplitPane(Conversation(state, flows, approvals), new SplitPane(graph, right, 640), 320));
    }

    // The describe/change dialogue: the sentence, then each build flow as a turn with its link to the Tasks board and its consent.
    public static Node Conversation(CreatorState state, FlowView[] flows, Approval[] approvals)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(flows);
        List<Node> children =
        [
            new Text(state.ConversationId.Length > 0 ? $"Conversation {state.ConversationId}" : "Describe what to make.") { Tone = "muted" },
            new Field(DescribeField, FieldKey(DescribeField), FieldKind.Text, "Describe a unit…", OnSubmit: AppSignals.Emit(App, "make", state.Draft)),
            new Button("make", "Make", Emit: AppSignals.Emit(App, "make", state.Draft), Tone: ButtonTone.Accent) { Small = true, Icon = "sparkles" },
            new Text(state.Status),
            new Divider("STARTER TEMPLATES"),
            new Row([.. ShippedUnits.All.Select(alias => new Button($"seed:{alias}", ShippedUnits.Title(alias), Emit: AppSignals.Emit(App, "seed", alias), Tone: ButtonTone.Neutral) { Small = true })]) { Gap = 6 },
            new Divider("SHIPPED UNIT TEMPLATES"),
            new Row([.. ShippedUnits.Legacy.Select(alias => new Button($"seed:{alias}", ShippedUnits.Title(alias), Emit: AppSignals.Emit(App, "seed", alias), Tone: ButtonTone.Neutral) { Small = true })]) { Gap = 6 },
        ];
        foreach (var flow in flows.Where(f => f.Type == BuildFlow.Alias).OrderByDescending(f => f.Started))
        {
            children.Add(Turn(flow, approvals));
        }
        return new Column(children);
    }

    private static Node Turn(FlowView flow, Approval[] approvals)
    {
        List<Node> children =
        [
            new Button($"flow:{flow.FlowId}", "Open on the Tasks board", Intent: AppSignals.OpenApp("tasks", flow.FlowId)) { Small = true, Icon = "tasks" },
        ];
        if (Consent(flow, approvals) is { } consent)
        {
            // The install consent is answered in the inbox, as on the Tasks board: read-only here.
            children.Add(new ApprovalCardNode($"consent:{flow.FlowId}", consent.PlanId, consent.Title, consent.Detail, []));
            children.Add(new Text("Approve or deny in the inbox.") { Tone = "muted" });
        }
        var step = flow.Current is { } current ? $" · {current}" : "";
        return new Card(flow.Title, $"{flow.Status.ToString().ToLowerInvariant()}{step}{(flow.Subject is { } alias ? $" · {alias}" : "")}", children);
    }

    // The SynapseStaged consent a build flow's install waits on.
    public static Approval? Consent(FlowView flow, Approval[] approvals)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(approvals);
        return flow is { Status: FlowStatus.Waiting, WaitingStep: "install" }
            ? approvals.FirstOrDefault(a => a.Kind == ApprovalKind.SynapseStaged && a.PlanId == flow.ConfirmationPlanId)
            : null;
    }
}
