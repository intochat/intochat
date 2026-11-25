using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.UI;
using AppRef = IntoChat.OS.Host.Primitives.AppRef;
using IntoChat.OS.Host.Visuals;
using IntoChat.OS.Host.Primitives;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Creator;

public static class CreatorView
{
    public const string App = "creator";
    public const string ToggleStateGraphTarget = "view:state-graph";

    // Brain-local keys for Creator's live Modules.UI time-travel & inspector neurons.
    public const string ReplaySliderKey = "creator/replay/slider";
    public const string ReplayCommandBarKey = "creator/replay/transport";
    public const string InspectorTabsKey = "creator/inspector/tabs";
    public const string InspectorStateKey = "creator/inspector/state";
    public const string InspectorSourceKey = "creator/inspector/source";
    public const string InspectorSignalsKey = "creator/inspector/signals";

    // Child keys of Creator's control.tabStrip selectors: the graph mode in the toolbar and the live preview's viewport width.
    public const string GraphModeKey = "creator-graph-mode";
    public const string ViewportKey = "creator-viewport";

    public static View Render(
        CreatorWorkspaceState state,
        BrainTopology topology,
        FlowView[] flows,
        Approval[] approvals,
        Inspection inspection,
        IReadOnlyList<TraceHop>? timelineHops = null,
        IntoChat.OS.Modules.Apps.Synapses.SynapseDefinition[]? synapses = null,
        string transition = "",
        DigitalBrain.Units.DeployedSource[]? deployedUnits = null,
        Func<string, TabStripProps, Node>? tabStrip = null)
        => new("Creator", RenderNode(state, topology, flows, approvals, inspection, timelineHops ?? inspection.Hops, synapses, transition, deployedUnits, tabStrip));

    // The selectors are tab strip children of the Creator screen, which passes its Child for tabStrip; a view drawn without the
    // screen (a static render) has no children and leaves them out.

    public static Node RenderNode(
        CreatorWorkspaceState state,
        BrainTopology topology,
        FlowView[] flows,
        Approval[] approvals,
        Inspection inspection,
        IReadOnlyList<TraceHop> timelineHops,
        IntoChat.OS.Modules.Apps.Synapses.SynapseDefinition[]? synapses = null,
        string transition = "",
        DigitalBrain.Units.DeployedSource[]? deployedUnits = null,
        Func<string, TabStripProps, Node>? tabStrip = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(flows);
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(timelineHops);

        var toolbar = TopToolbar(state, timelineHops, tabStrip);
        var left = LeftPane(state, topology, flows, approvals, synapses, deployedUnits);
        var center = CenterPane(state, topology, inspection, timelineHops, flows, tabStrip);
        var right = CreatorInspector.Render(state, inspection, timelineHops, flows, approvals);

        return AppShell(
            key: "creator-shell",
            sidebar: left,
            body: center,
            toolbar: toolbar,
            inspector: right,
            automationName: "Creator Topology & State Studio",
            transition: transition);
    }

    public static Node TopToolbar(CreatorWorkspaceState state, IReadOnlyList<TraceHop> timelineHops, Func<string, TabStripProps, Node>? tabStrip = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(timelineHops);

        var activeGraphMode = state.ShowStateGraph || state.CanvasMode == "state" ? "state" : "brain";
        var modeSwitcher = tabStrip?.Invoke(GraphModeKey, new TabStripProps(
            [new("brain", "Brain Graph"), new("state", "Accordant State Graph")],
            AutomationName: "Graph mode",
            SelectedId: activeGraphMode));

        var steps = ReplayScript.Steps(timelineHops);
        var hasEvents = steps.Length > 0;
        var cursorReadout = ReplayScript.FormatCursorReadout(state.ReplayCursor, steps.Length);
        var toggleLabel = state.IsReplayActive ? "Live" : "Replay";
        var toggleIcon = state.IsReplayActive ? "play" : "pause";

        Node[] items =
        [
            // A zero spacer keeps the slider's extent index when the view is drawn without the screen.
            modeSwitcher ?? new Spacer(0),
            Button("First", "chevrons-left")
                .Key("replay-first")
                .Small()
                .Disabled(!hasEvents)
                .Emits(AppSignals.Emit(App, "replay", "first")),
            Button("Prev", "chevron-left")
                .Key("replay-prev")
                .Small()
                .Disabled(!hasEvents)
                .Emits(AppSignals.Emit(App, "replay", "prev")),
            PrimaryButton(toggleLabel, toggleIcon)
                .Key("replay-toggle")
                .Small()
                .Disabled(!hasEvents && !state.IsReplayActive)
                .Emits(AppSignals.Emit(App, "replay", "toggle")),
            Button("Next", "chevron-right")
                .Key("replay-next")
                .Small()
                .Disabled(!hasEvents)
                .Emits(AppSignals.Emit(App, "replay", "next")),
            Button("Last", "chevrons-right")
                .Key("replay-last")
                .Small()
                .Disabled(!hasEvents)
                .Emits(AppSignals.Emit(App, "replay", "last")),
            GhostButton("Stop", "radio")
                .Key("replay-live")
                .Small()
                .Disabled(!state.IsReplayActive)
                .Emits(AppSignals.Emit(App, "replay", "stop")),
            GhostButton(cursorReadout, "history")
                .Key("replay-cursor-readout")
                .Small()
                .Emits(AppSignals.Emit(App, "replay", "toggle")),
            new NeuronRef("ui.slider", ReplaySliderKey),
            new Button("open-engineer-studio", "Open Studio (Engineer) ↗", Intent: AppSignals.OpenApp("engineer", state.ActiveRunId))
            {
                Small = true,
                Icon = Icons.Sparkles,
            },
        ];

        // Extent index 8 gives `creator/replay/slider` a compact 144px inline width inside the single <=44px row.
        double[] extents = [0, 0, 0, 0, 0, 0, 0, 0, 144, 0];
        return new Row(items) { Gap = Space.Xs, Extents = extents };
    }

    public static Node LeftPane(
        CreatorWorkspaceState state,
        BrainTopology topology,
        FlowView[] flows,
        Approval[] approvals,
        IntoChat.OS.Modules.Apps.Synapses.SynapseDefinition[]? synapses = null,
        DigitalBrain.Units.DeployedSource[]? deployedUnits = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(flows);
        ArgumentNullException.ThrowIfNull(approvals);

        var children = new List<Node>();

        // 1. Installed Apps (Clean vertical NavItems — zero horizontal overflow!)
        var appNodes = topology.Nodes
            .Where(n => n.Role == BrainGraph.App || AppRef.TryParse(n.Id, out _))
            .Where(n => !AppRef.TryParse(n.Id, out var a) || a.Name != App)
            .Take(16)
            .ToArray();

        children.Add(SectionHeader("Apps"));
        if (appNodes.Length > 0)
        {
            foreach (var node in appNodes)
            {
                var label = node.Label.Length > 0
                    ? node.Label
                    : AppRef.TryParse(node.Id, out var nodeRef) ? nodeRef.Name : node.Id;
                var isSelected = state.Selected is { Kind: Selection.Node } sel && sel.Id == node.Id;
                children.Add(NavItem(
                    key: $"app:{node.Id}",
                    label: label,
                    icon: Icons.LayoutGrid,
                    action: BrainGraph.NodeKey(node.Id),
                    selected: isSelected,
                    emit: AppSignals.Emit(App, "select", BrainGraph.NodeKey(node.Id))));
            }
        }
        else
        {
            children.Add(MutedText("No external apps installed."));
        }

        // 2. Catalog & Deployed Runtime Units (Clean vertical NavItems)
        var unitNodes = topology.Nodes
            .Where(n => n.Role == BrainGraph.Unit || (!n.Id.StartsWith("draft:", StringComparison.Ordinal) && !AppRef.TryParse(n.Id, out _)))
            .Take(16)
            .ToArray();

        var knownAliases = new HashSet<string>(
            unitNodes.Select(n => BrainGraph.AliasOf(n.Id) ?? n.Id),
            StringComparer.Ordinal);

        var extraDeployed = (deployedUnits ?? [])
            .Select(u => (Unit: u, Alias: u.Aliases.FirstOrDefault() ?? u.SourceKey))
            .Where(x => !string.IsNullOrWhiteSpace(x.Alias) && knownAliases.Add(x.Alias))
            .Take(16)
            .ToArray();

        children.Add(SectionHeader("Units"));
        if (unitNodes.Length > 0 || extraDeployed.Length > 0)
        {
            foreach (var node in unitNodes)
            {
                var alias = BrainGraph.AliasOf(node.Id) ?? node.Id;
                var isSelected = state.Selected is { Kind: Selection.Node } sel && sel.Id == node.Id;
                children.Add(NavItem(
                    key: $"unit:{node.Id}",
                    label: alias,
                    icon: "cpu",
                    action: BrainGraph.NodeKey(node.Id),
                    selected: isSelected,
                    emit: AppSignals.Emit(App, "select", BrainGraph.NodeKey(node.Id))));
            }

            foreach (var (deployed, alias) in extraDeployed)
            {
                var synKey = $"{Selection.SynapsePrefix}{alias}";
                var isSelected = state.Selected is { Kind: Selection.Node } sel
                    && (string.Equals(sel.Id, synKey, StringComparison.Ordinal)
                        || string.Equals(sel.Id, $"{Selection.SynapsePrefix}reaction:{alias}", StringComparison.Ordinal));
                children.Add(NavItem(
                    key: $"unit:{alias}",
                    label: alias,
                    icon: "cpu",
                    action: synKey,
                    selected: isSelected,
                    badge: deployed.Status.ToString(),
                    emit: AppSignals.Emit(App, "select", synKey)));
            }
        }
        else
        {
            children.Add(MutedText("No runtime units installed yet."));
        }

        // 3. Active Flows (Vertical list covering EngineerFlow, ChangeFlow, and BuildFlow)
        children.Add(SectionHeader("Active Flows"));
        var activeFlows = flows
            .Where(f => f.Type is IEngineerRuns.EngineerFlow or IEngineerRuns.ChangeFlow or BuildInner.Flow)
            .OrderByDescending(f => f.Started)
            .Take(10)
            .ToArray();

        if (activeFlows.Length > 0)
        {
            foreach (var flow in activeFlows)
            {
                var isSelectedRun = string.Equals(state.ActiveRunId, flow.FlowId, StringComparison.Ordinal);
                children.Add(NavItem(
                    key: $"run:{flow.FlowId}",
                    label: $"{flow.Title} · {flow.FlowId}",
                    icon: "workflow",
                    action: flow.FlowId,
                    selected: isSelectedRun,
                    badge: flow.Status.ToString(),
                    emit: AppSignals.Emit(App, "run", flow.FlowId)));

                if (Consent(flow, approvals) is { } consent)
                {
                    var visual = VisualCatalog.ForApproval(consent.Subject);
                    children.Add(new ApprovalCardNode(
                        $"consent:{flow.FlowId}",
                        consent.PlanId,
                        consent.Title,
                        consent.Detail,
                        [],
                        Icon: visual.IconKey,
                        CapabilityLabel: visual.Label));
                }
            }
        }
        else
        {
            children.Add(MutedText("No active flows."));
        }

        if (synapses is { Length: > 0 })
        {
            children.Add(SectionHeader("Synapses"));
            foreach (var synapse in synapses.OrderBy(static s => s.Name, StringComparer.Ordinal).Take(8))
            {
                var synTarget = $"{Selection.SynapsePrefix}{synapse.Id}";
                if (synapse.Form == IntoChat.OS.Modules.Apps.Synapses.SynapseForm.Workflow)
                {
                    children.Add(HStack(Space.Xs)[
                        Button($"Run {synapse.Name}", Icons.Play)
                            .Key($"synapse:{synapse.Id}")
                            .Small()
                            .Emits(AppSignals.Emit(App, "start-workflow", synapse.Name)),
                        GhostButton("3D", "git-branch")
                            .Key($"inspect:{synapse.Id}")
                            .Small()
                            .Emits(AppSignals.Emit(App, "select", synTarget))
                    ]);
                }
                else
                {
                    var isSelectedSynapse = state.Selected is { Kind: Selection.Node } sel
                        && (string.Equals(sel.Id, synTarget, StringComparison.Ordinal)
                            || string.Equals(BrainGraph.AliasOf(sel.Id), synapse.Name, StringComparison.Ordinal));
                    var badge = synapse.LastRun
                        ?? (synapse.Runs > 0 ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{synapse.Runs}x") : null);
                    children.Add(NavItem(
                        key: $"synapse:{synapse.Id}",
                        label: $"{synapse.Name} · {synapse.Form}",
                        icon: IconForSynapseForm(synapse.Form),
                        action: synTarget,
                        selected: isSelectedSynapse,
                        badge: badge,
                        emit: AppSignals.Emit(App, "select", synTarget)));
                }
            }
        }

        children.Add(HStack(Space.Xs)[
            StatusPill(
                approvals.Length > 0 ? StatusKind.NeedsYou : (state.IsReplayActive ? StatusKind.Working : StatusKind.Idle),
                approvals.Length > 0 ? $"{approvals.Length} Approval(s)" : (state.IsReplayActive ? "Replay" : "Live")),
            MutedText(state.Status)
        ]);

        return VStack(Space.Xs, [.. children]);
    }

    public static Node CenterPane(
        CreatorWorkspaceState state,
        BrainTopology topology,
        Inspection inspection,
        IReadOnlyList<TraceHop> timelineHops,
        FlowView[]? flows = null,
        Func<string, TabStripProps, Node>? tabStrip = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(timelineHops);

        var effectiveFlows = flows ?? [.. topology.Flows];

        if (state.CanvasMode == "live")
        {
            return RenderLiveStudioPreview(state, inspection, tabStrip);
        }

        if (state.CanvasMode == "state" || state.ShowStateGraph)
        {
            return CreatorStateSpaceInspector.HasInspectableUnitOrSynapse(state, inspection, effectiveFlows)
                ? CreatorStateSpaceInspector.RenderInspectionScene(state, inspection, effectiveFlows)
                : StateGraphView(CreatorStateSpaceInspector.CreatorGraph);
        }

        if (timelineHops.Count == 0 && CreatorStateSpaceInspector.ShouldProjectLiveSynapseScene(state, inspection, effectiveFlows))
        {
            return CreatorStateSpaceInspector.RenderInspectionScene(state, inspection, effectiveFlows);
        }

        var graph = BrainGraph.Build(topology, state.Selected is { Kind: Selection.Node } node ? node.Id : null);
        var replayTraceId = state.TraceId ?? (inspection.Kind == Selection.Pulse ? inspection.Id : "timeline");
        if (timelineHops.Count > 0)
        {
            graph = graph with
            {
                Synapses = [.. graph.Synapses, .. ReplayScript.Synapses(timelineHops, replayTraceId, state.ReplayCursor, topology)],
            };
        }

        // Return BrainSceneNode directly so AppShellNode + NodeRenderer.fills stretches it to 100% window height!
        return graph;
    }

    private static string IconForSynapseForm(IntoChat.OS.Modules.Apps.Synapses.SynapseForm form) => form switch
    {
        IntoChat.OS.Modules.Apps.Synapses.SynapseForm.App => Icons.LayoutGrid,
        IntoChat.OS.Modules.Apps.Synapses.SynapseForm.Flow => "workflow",
        IntoChat.OS.Modules.Apps.Synapses.SynapseForm.Workflow => Icons.Play,
        IntoChat.OS.Modules.Apps.Synapses.SynapseForm.Agent => Icons.Sparkles,
        _ => "cpu",
    };

    public static Approval? Consent(FlowView flow, Approval[] approvals)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(approvals);
        return flow is { Status: FlowStatus.Waiting, WaitingStep: "install" }
            ? approvals.FirstOrDefault(a => a.Kind == ApprovalKind.SynapseStaged && a.PlanId == flow.ConfirmationPlanId)
            : null;
    }

    private static bool TryResolveLivePreviewTarget(
        CreatorWorkspaceState state,
        Inspection inspection,
        out string slotUri,
        out string slotKind,
        out string? openAppId,
        out string displayTitle)
    {
        slotUri = string.Empty;
        slotKind = "view";
        openAppId = null;
        displayTitle = string.Empty;

        if (state.Selected is not { Kind: Selection.Node, Id: { Length: > 0 } nodeId })
        {
            return false;
        }

        if (AppRef.TryParse(nodeId, out var appRef) && appRef.Name != App)
        {
            slotUri = appRef.ChildKey("view");
            slotKind = "app";
            openAppId = appRef.Name;
            displayTitle = appRef.Name;
            return true;
        }

        if (!nodeId.StartsWith("draft:", StringComparison.Ordinal) && inspection.Alias is { Length: > 0 } unitAlias)
        {
            var slash = nodeId.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0 || !BrainId.IsValid(nodeId[..slash])) { return false; }
            slotUri = new NeuronId(new BrainId(nodeId[..slash]), "units", $"{unitAlias}/view").ToString();
            slotKind = "unit";
            openAppId = unitAlias;
            displayTitle = unitAlias;
            return true;
        }

        return false;
    }

    private static Node RenderLiveStudioPreview(CreatorWorkspaceState state, Inspection inspection, Func<string, TabStripProps, Node>? tabStrip)
    {
        if (!TryResolveLivePreviewTarget(state, inspection, out var slotUri, out var slotKind, out var openAppId, out var title))
        {
            return Empty(
                "Select an App or Unit to Preview Live",
                "Pick any installed app or runtime unit in the left sidebar or Brain Graph to mount its live interactive View.");
        }

        double? viewportWidth = state.PreviewViewport switch
        {
            "390" => 390,
            "720" => 720,
            _ => null,
        };

        var viewportBar = HStack(Space.Xs)[
            Button("Back to 3D Graph", "git-branch")
                .Key("preview-back-graph")
                .Small()
                .Emits(AppSignals.Emit(App, "select", "mode:brain")),
            tabStrip?.Invoke(ViewportKey, new TabStripProps(
                [new("fill", "Fill"), new("720", "720px"), new("390", "390px")],
                AutomationName: "Preview width",
                SelectedId: state.PreviewViewport)),
            openAppId is { Length: > 0 }
                ? new Button("studio-open-window", $"Open {openAppId} ↗", Intent: AppSignals.OpenApp(openAppId)) { Small = true, Icon = "external-link" }
                : null
        ];

        return VStack(Space.Sm)[
            viewportBar,
            StudioFrame($"Live Studio · {title}", $"Mounted via Slot(\"{slotUri}\")", viewportWidth, Slot(slotUri, slotKind))
        ];
    }
}
