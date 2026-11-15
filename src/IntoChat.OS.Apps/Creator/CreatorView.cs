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

        return new View("Creator", ResponsiveSplit(left, ResponsiveSplit(center, right, 680), 300));
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

        // 1. Catalog Units & Apps List
        children.Add(SectionHeader("Catalog Units"));
        var unitNodes = topology.Nodes
            .Where(n => n.Role == BrainGraph.Unit || (!n.Id.StartsWith("draft:", StringComparison.Ordinal) && !n.Id.Contains("/apps/", StringComparison.Ordinal)))
            .Take(10)
            .ToArray();

        if (unitNodes.Length > 0)
        {
            var unitButtons = unitNodes.Select(node =>
            {
                var alias = BrainGraph.AliasOf(node.Id) ?? node.Id;
                var isSelected = state.Selected is { Kind: Selection.Node } sel && sel.Id == node.Id;
                return (Node)(isSelected ? PrimaryButton(alias) : Button(alias))
                    .Key($"unit:{node.Id}")
                    .Small()
                    .Emits(AppSignals.Emit(App, "select", BrainGraph.NodeKey(node.Id)));
            });
            children.Add(HStack(Space.Xs, [.. unitButtons]));
        }
        else
        {
            children.Add(MutedText("No runtime units installed yet."));
        }

        var appNodes = topology.Nodes
            .Where(n => n.Role == BrainGraph.App || n.Id.Contains("/apps/", StringComparison.Ordinal))
            .Where(n => !n.Id.EndsWith("/apps/creator", StringComparison.Ordinal))
            .Take(12)
            .ToArray();

        if (appNodes.Length > 0)
        {
            children.Add(SectionHeader("Installed Apps"));
            var appButtons = appNodes.Select(node =>
            {
                var label = node.Label.Length > 0 ? node.Label : node.Id.Split("/apps/").Last();
                var isSelected = state.Selected is { Kind: Selection.Node } sel && sel.Id == node.Id;
                return (Node)(isSelected ? PrimaryButton(label) : GhostButton(label))
                    .Key($"app:{node.Id}")
                    .Small()
                    .Emits(AppSignals.Emit(App, "select", BrainGraph.NodeKey(node.Id)));
            });
            children.Add(HStack(Space.Xs, [.. appButtons]));
        }

        // 2. Active Change Runs List
        children.Add(SectionHeader("Active Change Runs"));
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
        children.Add(SectionHeader("Make New Unit"));
        children.Add(HStack(Space.Sm)[
            StatusPill(
                approvals.Length > 0 ? StatusKind.NeedsYou : StatusKind.Idle,
                approvals.Length > 0 ? $"{approvals.Length} Approval(s)" : "Studio Ready"),
            MutedText(state.ConversationId.Length > 0 ? $"Ino · {state.ConversationId}" : "Describe what to make.")
        ]);
        children.Add(TextField(FieldKey(DescribeField), "e.g. Build a Pomodoro sprint timer…")
            .Key(DescribeField)
            .Label("Unit or App Goal")
            .Value(state.Draft)
            .Emits(AppSignals.Emit(App, "make", state.Draft)));
        children.Add(HStack(Space.Xs)[
            PrimaryButton("Make Unit", Icons.Sparkles)
                .Key("make")
                .Small()
                .Emits(AppSignals.Emit(App, "make", state.Draft)),
            new Button("open-ino-chat", "Co-Author in Ino", Intent: AppSignals.OpenApp("ino")) { Small = true, Icon = Icons.MessageSquare },
            new Button("open-gallery", "UI Kit Gallery", Intent: AppSignals.OpenApp("gallery")) { Small = true, Icon = Icons.LayoutGrid }
        ]);
        if (state.EmptyDraft.Broken)
        {
            children.Add(MutedText(state.EmptyDraft.Message));
        }
        children.Add(MutedText(state.Status));

        // 4. Starter and Shipped Templates
        children.Add(SectionHeader("Starter Templates"));
        children.Add(HStack(6, [.. ShippedUnits.All.Select(alias =>
            (Node)Button(ShippedUnits.Title(alias))
                .Key($"seed:{alias}")
                .Small()
                .Emits(AppSignals.Emit(App, "seed", alias)))]));

        children.Add(SectionHeader("Shipped Unit Templates"));
        children.Add(HStack(6, [.. ShippedUnits.Legacy.Select(alias =>
            (Node)Button(ShippedUnits.Title(alias))
                .Key($"seed:{alias}")
                .Small()
                .Emits(AppSignals.Emit(App, "seed", alias)))]));

        return VStack(Space.Sm, [.. children]);
    }

    public static Node CenterPane(
        CreatorState state,
        BrainTopology topology,
        FlowView[] flows,
        Inspection inspection)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(topology);

        var mode = state.ShowStateGraph && state.CanvasMode == "brain" ? "state" : state.CanvasMode;
        var modeSwitcher = Segmented(
            mode,
            Option("brain", "Brain Topology").Key("mode-brain").Emits(AppSignals.Emit(App, "select", "mode:brain")),
            Option("live", "Live Studio").Key("mode-live").Emits(AppSignals.Emit(App, "select", "mode:live")),
            Option("state", "State Graph").Key("mode-state").Emits(AppSignals.Emit(App, "select", "mode:state")),
            Option("source", "Source & Kit").Key("mode-source").Emits(AppSignals.Emit(App, "select", "mode:source")));

        var children = new List<Node>
        {
            Toolbar(
                modeSwitcher,
                FlexSpacer(),
                StatusPill(
                    mode == "state" ? StatusKind.Working : StatusKind.Done,
                    mode switch
                    {
                        "live" => "Interactive App Studio",
                        "state" => "Accordant State Space",
                        "source" => "C# Source & Kit v2",
                        _ => "Live Brain Topology",
                    }),
                Button(
                    state.ShowStateGraph ? "Show Live Brain Graph" : "Show Accordant State Graph",
                    state.ShowStateGraph ? "activity" : "git-branch")
                    .Key("toggle-state-graph")
                    .Small()
                    .Emits(AppSignals.Emit(App, "select", ToggleStateGraphTarget)))
        };

        // 1. Primary Studio Canvas
        if (mode == "live")
        {
            children.Add(RenderLiveStudioPreview(state, inspection));
        }
        else if (mode == "source")
        {
            children.Add(RenderSourceAndKitStudio(inspection));
        }
        else if (mode == "state" || state.ShowStateGraph)
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

            // When a compiled app or live unit is selected in Brain mode, also offer an instant one-click Live Preview card!
            if (TryResolveLivePreviewTarget(state, inspection, out var quickSlotUri, out var quickSlotKind, out var quickAppName, out var quickTitle))
            {
                children.Add(RenderEmbeddedStudioCard(state, quickTitle, quickSlotUri, quickSlotKind, quickAppName));
            }
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

        // 3. Active ChangeFlow / BuildFlow Pipeline (ONLY rendered when an actual run exists — zero fake placeholder diffs!)
        var activeRun = flows.FirstOrDefault(f => f.FlowId == state.ActiveRunId || f.Type == ChangeFlow.Alias);
        if (activeRun is not null)
        {
            children.Add(SectionHeader("Change Pipeline (7 Stages)"));
            children.Add(RenderPipeline(activeRun));

            children.Add(SectionHeader("Unified Diff Review"));
            children.Add(RenderDiffViewer(activeRun));

            children.Add(SectionHeader("Build & Test Summary"));
            children.Add(RenderBuildAndTestSummary(activeRun));
        }

        return VStack(Space.Md, [.. children]);
    }

    private static bool TryResolveLivePreviewTarget(
        CreatorState state,
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

        if (nodeId.Split("/apps/") is [var brain, var appName] && appName.Length > 0 && appName != App)
        {
            slotUri = $"{brain}/apps/{appName}/view";
            slotKind = "app";
            openAppId = appName;
            displayTitle = appName;
            return true;
        }

        if (!nodeId.StartsWith("draft:", StringComparison.Ordinal) && inspection.Alias is { Length: > 0 } unitAlias)
        {
            var slash = nodeId.IndexOf('/', StringComparison.Ordinal);
            var unitBrain = slash > 0 ? nodeId[..slash] : "local";
            slotUri = $"{unitBrain}/units/{unitAlias}/view";
            slotKind = "unit";
            openAppId = unitAlias;
            displayTitle = unitAlias;
            return true;
        }

        return false;
    }

    private static Node RenderLiveStudioPreview(CreatorState state, Inspection inspection)
    {
        if (!TryResolveLivePreviewTarget(state, inspection, out var slotUri, out var slotKind, out var openAppId, out var title))
        {
            return Empty(
                "Select an App or Unit to Preview Live",
                "Pick any installed app or runtime unit in the left sidebar or Brain Topology to mount its live interactive View inside this Studio viewport — or describe a new unit to synthesize one with Ino.");
        }

        return RenderEmbeddedStudioCard(state, title, slotUri, slotKind, openAppId);
    }

    private static Node RenderEmbeddedStudioCard(CreatorState state, string title, string slotUri, string slotKind, string? openAppId)
    {
        double? viewportWidth = state.PreviewViewport switch
        {
            "390" => 390,
            "720" => 720,
            _ => null,
        };

        var viewportBar = Toolbar(
            Segmented(
                "Viewport",
                state.PreviewViewport,
                Option("fill", "Desktop Fill").Key("vp-fill").Emits(AppSignals.Emit(App, "select", "viewport:fill")),
                Option("720", "Tablet 720px").Key("vp-720").Emits(AppSignals.Emit(App, "select", "viewport:720")),
                Option("390", "Compact 390px").Key("vp-390").Emits(AppSignals.Emit(App, "select", "viewport:390"))),
            FlexSpacer(),
            openAppId is { Length: > 0 }
                ? new Button("studio-open-window", $"Open {openAppId} Window", Intent: AppSignals.OpenApp(openAppId)) { Small = true, Icon = "external-link" }
                : null);

        return VStack(Space.Sm)[
            viewportBar,
            StudioFrame($"Live Studio · {title}", $"Mounted via Slot(\"{slotUri}\")", viewportWidth, Slot(slotUri, slotKind))
        ];
    }

    private static Node RenderSourceAndKitStudio(Inspection inspection)
    {
        const string kitQuickRef = """
            using DigitalBrain.Rules;
            using DigitalBrain.UI;
            using IntoChat.OS.UI;
            using static IntoChat.OS.UI.Kit;

            // 1. Immutable State + Domain Rules (Accordant DDD)
            public sealed record PomodoroState(int Minutes = 25, bool Running = false)
            {
                public Rule ValidDuration(int mins) =>
                    Decision.Rule("duration_bounds", mins is < 1 or > 120, "Choose 1–120 minutes.");

                public Decision<PomodoroState> SetDuration(int mins) =>
                    Decision.Check("set-duration", ValidDuration(mins))
                    ?? Decision.Accept(this with { Minutes = mins });
            }

            // 2. Pure C# View with Kit v2 Controls
            VStack(Space.Md)[
                HeaderBar("Pomodoro Studio", $"{state.Minutes} min sprint",
                    StatusPill(state.Running ? StatusKind.Working : StatusKind.Idle, state.Running ? "Focusing" : "Ready"),
                    PrimaryButton(state.Running ? "Pause" : "Start", Icons.Play).Key("toggle")),
                Toolbar(
                    SectionHeader("Preset Sprints"),
                    Segmented($"{state.Minutes}", Option("25", "25m"), Option("50", "50m"))),
                Expander("Session Notes", "Optional markdown scratchpad", expanded: false,
                    MultilineField("Notes", "What are you shipping this sprint?").Label("Goal"))
            ]
            """;

        var blocks = new List<Node>();
        if (inspection.Files.Count > 0)
        {
            foreach (var (name, source) in inspection.Files.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var lang = name.EndsWith(".cs", StringComparison.Ordinal) ? "csharp"
                    : name.EndsWith(".json", StringComparison.Ordinal) ? "json"
                    : "markdown";
                blocks.Add(CodeBlock(name, source, lang, maxHeight: 340));
            }
        }

        blocks.Add(CodeBlock("IntoChat.OS.UI.Kit v2 Quick Reference", kitQuickRef, "csharp", maxHeight: 320));
        return VStack(Space.Md, [.. blocks]);
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

    private static Node RenderDiffViewer(FlowView flow)
    {
        var subject = flow.Subject ?? "unit";
        var summary = flow.Detail ?? $"Change flow '{flow.Title}' at stage '{flow.Current ?? "completed"}'.";
        return Card("Unified Diff", $"{flow.Title} ({flow.FlowId})")[
            HStack(Space.Sm)[
                StatusPill(flow.Status == FlowStatus.Failed ? StatusKind.Error : StatusKind.Done, $"{flow.Done.Length}/{flow.Steps.Length} steps done"),
                StatusPill(StatusKind.Idle, $"Target: {subject}")
            ],
            MutedText(summary)
        ];
    }

    private static Node RenderBuildAndTestSummary(FlowView flow)
    {
        var completedCount = flow.Done.Length;
        var totalCount = Math.Max(completedCount, flow.Steps.Length);
        var failed = flow.Status == FlowStatus.Failed;
        var cancelled = flow.Status == FlowStatus.Cancelled;
        var completed = flow.Status == FlowStatus.Completed;
        var summaryText = !string.IsNullOrWhiteSpace(flow.Detail)
            ? flow.Detail
            : flow.Status switch
            {
                FlowStatus.Completed => $"Run {flow.FlowId}: Completed ({completedCount}/{totalCount} steps).",
                FlowStatus.Waiting => $"Run {flow.FlowId}: Waiting on {flow.WaitingStep ?? "approval"} ({completedCount}/{totalCount} steps).",
                FlowStatus.Failed => $"Run {flow.FlowId}: Failed.",
                FlowStatus.Cancelled => $"Run {flow.FlowId}: Cancelled.",
                _ => $"Run {flow.FlowId}: {flow.Current ?? "Running"} ({completedCount}/{totalCount} steps).",
            };

        var buildRecorded = flow.Done.Contains("build");
        var testRecorded = flow.Done.Contains("test");
        var stagePillKind = failed || cancelled ? StatusKind.Error : (completed ? StatusKind.Done : StatusKind.Idle);
        var stageLabel = flow.Status.ToString();

        return HStack(Space.Sm)[
            Card("Flow Status", summaryText)[
                StatusPill(stagePillKind, stageLabel)
            ],
            Card(
                "Recorded Stages",
                flow.Done.Length > 0
                    ? $"Completed: {string.Join(", ", flow.Done)}"
                    : $"Pending first stage ({flow.Current ?? "initializing"}).")[
                StatusPill(
                    buildRecorded && testRecorded ? StatusKind.Done : stagePillKind,
                    $"{completedCount}/{totalCount} done")
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
