using System.Globalization;
using DigitalBrain.Flows;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.UI;
using IntoChat.OS.Host.Visuals;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Creator;

public static class CreatorInspector
{
    private const string App = CreatorView.App;

    public static Node Render(
        CreatorWorkspaceState state,
        Inspection inspection,
        IReadOnlyList<TraceHop> timelineHops,
        FlowView[] flows,
        Approval[] approvals,
        CreatorView.Children? controls = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(timelineHops);
        ArgumentNullException.ThrowIfNull(flows);
        ArgumentNullException.ThrowIfNull(approvals);

        List<Node> children =
        [
            HStack(Space.Xs)[
                SectionHeader("Inspector"),
                FlexSpacer(),
                GhostButton("Clear selection", "x")
                    .Key("clear-selection")
                    .Small()
                    .Sends(new CreatorSelect())
            ],
        ];
        if (controls?.TabStrip(CreatorView.InspectorTabsKey, InspectorTabsOf(state)) is { } tabs)
        {
            children.Add(tabs);
        }

        if (inspection.Notice is { Length: > 0 } notice)
        {
            children.Add(Banner("Note", notice));
        }

        children.AddRange(state.InspectorTab switch
        {
            "source" => RenderSourceTab(state, inspection),
            "signals" => RenderSignalsTab(state, inspection, timelineHops, controls),
            "state-graph" => RenderStateGraphTab(state, inspection, flows),
            "scenarios" => RenderScenariosTab(state, inspection),
            _ => RenderStateTab(state, inspection, timelineHops, flows),
        });

        RenderApprovalGates(children, state, inspection, flows, approvals);

        return VStack(Space.Sm, [.. children]);
    }

    private static IEnumerable<Node> RenderStateTab(
        CreatorWorkspaceState state,
        Inspection inspection,
        IReadOnlyList<TraceHop> timelineHops,
        FlowView[] flows)
    {
        if (state.Selected is { Kind: Selection.Node })
        {
            if (inspection.Alias is { } alias)
            {
                var ghost = inspection.Id.StartsWith("draft:", StringComparison.Ordinal);
                yield return HStack(Space.Xs)[
                    StatusPill(ghost ? StatusKind.Working : StatusKind.Done, ghost ? $"Building · {alias}" : alias),
                    MutedText(inspection.Id)
                ];

                if (!ghost)
                {
                    yield return HStack(Space.Xs)[
                        Button("Preview Live", "eye")
                            .Key("preview-unit-live")
                            .Small()
                            .Sends(new CreatorSelect("mode:live")),
                        new Button("open-unit-window", $"Open {alias}", Intent: AppSignals.OpenApp(alias)) { Small = true, Icon = "external-link" },
                        Button("Share as App-Post", "share-2")
                            .Key("share-unit")
                            .Small()
                            .Sends(new CreatorShare(alias))
                    ];
                }

                var flow = flows.Where(f => f.Subject == alias).OrderByDescending(f => f.Started).FirstOrDefault();
                if (flow is not null)
                {
                    yield return new Button("open-unit-flow", $"Build flow: {flow.Status.ToString().ToLowerInvariant()}", Intent: AppSignals.OpenApp("tasks", flow.FlowId))
                    {
                        Small = true,
                        Icon = "tasks",
                    };
                }
            }
            else
            {
                var isApp = IntoChat.OS.Host.Primitives.AppRef.TryParse(inspection.Id, out var appRef);
                yield return HStack(Space.Xs)[
                    StatusPill(StatusKind.Idle, isApp ? appRef.Name : inspection.Id),
                    MutedText(isApp ? "Compiled App" : "Kernel Neuron")
                ];
                if (isApp)
                {
                    var app = appRef.Name;
                    yield return HStack(Space.Xs)[
                        PrimaryButton($"Preview {app}", "eye")
                            .Key("preview-compiled-app")
                            .Small()
                            .Sends(new CreatorSelect("mode:live")),
                        new Button("open-app", $"Open {app}", Intent: AppSignals.OpenApp(app)) { Small = true, Icon = "external-link" }
                    ];
                }
            }
        }
        else if (state.Selected is null && timelineHops.Count == 0)
        {
            yield return Empty(
                "No Node Selected",
                "Select any unit, app, edge, or live signal pulse in the 3D Graph to inspect its state.");
        }

        var steps = ReplayScript.Steps(timelineHops);
        var cursor = Cursor(state, steps.Length);
        var title = cursor is { } at
            ? string.Create(CultureInfo.InvariantCulture, $"State · Event {at + 1} / {steps.Length}")
            : (inspection.Alias is { Length: > 0 } liveAlias ? $"Live State · {liveAlias}" : "Neuron State");
        yield return JsonTree(title, ReplayScript.StateJsonAt(steps, cursor, inspection.State));

        if (state.ReplayCursor is { } stepIdx && ReplayScript.Diff(steps, stepIdx) is { } diff)
        {
            yield return Table(
                string.Create(CultureInfo.InvariantCulture, $"State change at step {stepIdx + 1}"),
                ["Property", "Before", "After"],
                [.. diff.Select(row => (IReadOnlyList<string>)row)]);
        }
    }

    private static IEnumerable<Node> RenderSourceTab(CreatorWorkspaceState state, Inspection inspection)
    {
        var orderedFiles = OrderedFiles(inspection.FileMap());
        var selectedFile = ResolveActiveFile(state.SelectedSourceFile, orderedFiles);
        if (selectedFile is not null)
        {
            var passPct = CreatorStateSpaceInspector.ResolvePassPercent(inspection, state.Transcript);
            var buildBadge = inspection.DeployedUnit?.Hash is { Length: > 0 } hash
                ? $"build {hash[..Math.Min(8, hash.Length)]}"
                : (inspection.Synapse?.Form.ToString() ?? "Live Source");
            yield return HStack(Space.Xs)[
                StatusPill(passPct == 0 ? StatusKind.Error : StatusKind.Done, $"{selectedFile} · {passPct}%"),
                MutedText(buildBadge)
            ];
        }

        if (orderedFiles.Length > 1)
        {
            yield return HStack(Space.Xs, [.. orderedFiles.Select(file =>
                (Node)(string.Equals(file, selectedFile, StringComparison.Ordinal)
                    ? PrimaryButton(file)
                    : GhostButton(file))
                    .Key($"source-file:{file}")
                    .Small()
                    .Sends(new CreatorSelect($"file:{file}")))]);
        }

        if (selectedFile is not null && inspection.FileText(selectedFile) is { } content)
        {
            yield return CodeBlock(selectedFile, content, InferLanguage(selectedFile));
        }
        else
        {
            yield return MutedText("No source files kept for the current selection.");
        }
    }

    private static IEnumerable<Node> RenderSignalsTab(
        CreatorWorkspaceState state,
        Inspection inspection,
        IReadOnlyList<TraceHop> timelineHops,
        CreatorView.Children? controls)
    {
        if (controls?.Table(CreatorView.InspectorSignalsKey, SignalsTableOf(state, inspection, timelineHops)) is { } table)
        {
            yield return table;
        }

        if (inspection.Kind == Selection.Edge)
        {
            var parts = BrainGraph.ParseEdge(inspection.Id);
            yield return MutedText(parts is { } p ? $"{p.From} → {p.To}" : inspection.Id);

            foreach (var traceId in inspection.Signals.Select(s => s.TraceId).OfType<string>().Distinct(StringComparer.Ordinal).TakeLast(5))
            {
                yield return Button($"Trace {traceId}", "activity")
                    .Key($"open-trace:{traceId}")
                    .Small()
                    .Sends(new CreatorTrace(traceId))
                    .ToNode();
            }

            yield break;
        }

        if (timelineHops.Count == 0)
        {
            yield return MutedText("Select an edge or signal pulse on the graph to inspect its causal signal stream.");
            yield break;
        }

        var steps = ReplayScript.Steps(timelineHops);
        if (Cursor(state, steps.Length) is { } stepIdx && ReplayScript.Diff(steps, stepIdx) is { } diff)
        {
            yield return Table(
                string.Create(CultureInfo.InvariantCulture, $"State change at step {stepIdx + 1}"),
                ["Property", "Before", "After"],
                [.. diff.Select(row => (IReadOnlyList<string>)row)]);
        }

        foreach (var flowId in timelineHops.Select(h => h.FlowId).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            yield return new Button($"open-flow:{flowId}", "Open the flow", Intent: AppSignals.OpenApp("tasks", flowId))
            {
                Small = true,
                Icon = "tasks",
            };
        }
    }

    private static IEnumerable<Node> RenderStateGraphTab(CreatorWorkspaceState state, Inspection inspection, FlowView[] flows)
    {
        if (CreatorStateSpaceInspector.HasInspectableUnitOrSynapse(state, inspection, flows))
        {
            var targetAlias = inspection.Alias ?? inspection.Synapse?.Name ?? state.ActiveRunId ?? "synapse";
            yield return CreatorStateSpaceInspector.RenderUnitSummaryCard(targetAlias, inspection.FileMap(), inspection, state.Transcript);
            yield return CreatorStateSpaceInspector.RenderInspectionScene(state, inspection, flows);
            yield break;
        }

        var creatorGraph = CreatorStateSpaceInspector.CreatorGraph;
        yield return HStack(Space.Xs)[
            StatusPill(
                creatorGraph.IsHealthy ? StatusKind.Done : StatusKind.Error,
                creatorGraph.IsHealthy ? "Invariants Healthy" : $"{creatorGraph.Violations.Count} Violation(s)"),
            MutedText(string.Create(
                CultureInfo.InvariantCulture,
                $"{creatorGraph.States.Count} states · {creatorGraph.AcceptedTransitions.Count} accepted / {creatorGraph.RejectedTransitions.Count} guarded"))
        ];
        yield return StateGraphView(creatorGraph);
    }

    private static IEnumerable<Node> RenderScenariosTab(CreatorWorkspaceState state, Inspection inspection)
    {
        var alias = inspection.Alias;
        yield return HStack(Space.Xs)[
            PrimaryButton("Run scenario", "play")
                .Key("run-scenario")
                .Small()
                .Guard(state.RequiresSelectedNode)
                .Sends(new CreatorScenario(alias ?? "")),
            MutedText(alias is { Length: > 0 } ? $"Unit: {alias}" : "Select an installed unit first.")
        ];

        if (inspection.FileText("scenarios.json") is { } scenariosJson && !string.IsNullOrWhiteSpace(scenariosJson))
        {
            yield return CodeBlock("scenarios.json", scenariosJson, "json", maxHeight: 180);
        }

        if (state.Transcript is { Length: > 0 } transcript)
        {
            yield return CodeBlock("Scenario transcript", transcript, "text", maxHeight: 240);
        }
    }

    private static void RenderApprovalGates(
        List<Node> children,
        CreatorWorkspaceState state,
        Inspection inspection,
        FlowView[] flows,
        Approval[] approvals)
    {
        if (approvals.Length == 0)
        {
            return;
        }

        var alias = inspection.Alias;
        var flow = alias is not null
            ? flows.Where(f => f.Subject == alias).OrderByDescending(f => f.Started).FirstOrDefault()
            : null;

        var relevant = state.Selected is null
            ? approvals
            : approvals.Where(a =>
                (alias is not null && a.Subject == alias) ||
                a.Subject == App ||
                a.Detail.Contains(App, StringComparison.OrdinalIgnoreCase) ||
                a.PlanId == flow?.ConfirmationPlanId).ToArray();

        if (relevant.Length == 0)
        {
            return;
        }

        children.Add(SectionHeader("Pending Approval Gate"));
        foreach (var apprv in relevant)
        {
            children.Add(StatusPill(StatusKind.NeedsYou, "Needs Approval"));
            var visual = VisualCatalog.ForApproval(apprv.Subject);
            children.Add(new ApprovalCardNode(
                $"gate:{apprv.PlanId}",
                apprv.PlanId,
                apprv.Title,
                apprv.Detail,
                [],
                Icon: visual.IconKey,
                CapabilityLabel: visual.Label));
            children.Add(HStack(6)[
                PrimaryButton("Approve & Promote", Icons.Check)
                    .Key($"approve:{apprv.PlanId}")
                    .Small()
                    .Sends(new CreatorApprove(apprv.PlanId)),
                Button("Reject & Abort", "x")
                    .Key($"reject:{apprv.PlanId}")
                    .Small()
                    .Sends(new CreatorReject(apprv.PlanId))
            ]);
        }
    }

    // Creator owns the tab: the strip draws it and signals the person's choice.
    public static TabStripProps InspectorTabsOf(CreatorWorkspaceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(
            [.. CreatorWorkspaceState.InspectorTabIds.Select(id => new TabStripItem(id, CreatorWorkspaceState.FormatInspectorTabTitle(id)))],
            AutomationName: "Inspector",
            SelectedId: state.InspectorTab);
    }

    // The signals of the selected edge, or else the timeline's hops, as inline rows keyed by sequence. Creator owns the selection: the
    // row of the replay cursor's step is selected, and choosing a row steps the cursor there.
    public static QueryTableProps SignalsTableOf(CreatorWorkspaceState state, Inspection inspection, IReadOnlyList<TraceHop> timelineHops)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(timelineHops);
        var steps = ReplayScript.Steps(timelineHops);
        var currentSeq = Cursor(state, steps.Length) is { } at ? steps[at].Sequence : (long?)null;

        QueryTableColumn[] columns;
        QueryTableRow[] rows;
        string title;
        if (inspection.Kind == Selection.Edge && inspection.Signals.Length > 0)
        {
            title = $"Signals · {inspection.Alias ?? inspection.Id}";
            columns = [new("seq", "#"), new("at", "At"), new("publisher", "Publisher"), new("trace", "Trace")];
            rows =
            [
                .. inspection.Signals.Select(s => new QueryTableRow(Seq(s.Sequence),
                [
                    Seq(s.Sequence),
                    s.At.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    s.Publisher,
                    s.TraceId ?? string.Empty,
                ])),
            ];
        }
        else if (timelineHops.Count > 0)
        {
            var first = timelineHops.Min(h => h.At);
            title = $"Trace {(state.TraceId is { Length: > 0 } tid ? tid : inspection.Id)}";
            columns = [new("seq", "#"), new("dt", "Δt"), new("publisher", "Publisher"), new("signal", "Signal"), new("cause", "Cause")];
            rows =
            [
                .. timelineHops.Select(h => new QueryTableRow(Seq(h.Sequence),
                [
                    (h.Sequence == currentSeq ? "▶ " : "") + Seq(h.Sequence),
                    string.Create(CultureInfo.InvariantCulture, $"+{(h.At - first).TotalMilliseconds:0}ms"),
                    TraceFold.Indent(h.Depth) + h.Publisher,
                    h.Alias,
                    h.Cause ?? string.Empty,
                ])),
            ];
        }
        else
        {
            title = "Causal Signal Stream";
            columns = [new("seq", "#"), new("signal", "Signal"), new("publisher", "Publisher")];
            rows = [];
        }

        var selected = currentSeq is { } seq && rows.Any(r => r.Id == Seq(seq)) ? Seq(seq) : null;
        return new QueryTableProps(title, columns, AutomationName: "Signals", Rows: rows, SelectedRowId: selected);
    }

    private static string Seq(long sequence) => sequence.ToString(CultureInfo.InvariantCulture);

    private static long? Cursor(CreatorWorkspaceState state, int stepCount)
        => state.ReplayCursor is { } c && stepCount > 0 ? Math.Clamp(c, 0, stepCount - 1) : null;

    internal static string[] OrderedFiles(IReadOnlyDictionary<string, string> files)
        => [.. new[] { "spec.md" }
            .Concat(files.Keys.Where(k => k.EndsWith(".cs", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            .Append("scenarios.json")
            .Concat(files.Keys.Order(StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Where(files.ContainsKey)];

    internal static string? ResolveActiveFile(string? preferred, string[] orderedFiles)
        => preferred is not null && orderedFiles.Contains(preferred, StringComparer.Ordinal)
            ? preferred
            : orderedFiles.FirstOrDefault(f => f.EndsWith(".cs", StringComparison.Ordinal)) ?? orderedFiles.FirstOrDefault();

    internal static string InferLanguage(string fileName)
        => fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? "csharp"
            : fileName.EndsWith(".dart", StringComparison.OrdinalIgnoreCase) ? "dart"
            : fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "json"
            : fileName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ? "sql"
            : fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ? "yaml"
            : fileName.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ? "typescript"
            : "markdown";
}
