using System.Globalization;
using System.Text.RegularExpressions;
using DigitalBrain.Flows;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Synapses;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Creator;

/// <summary>
/// Accordant-inspired State-Space &amp; Live Synapse 3D Graph Inspector for Creator: unfolds <see cref="CreatorWorkspaceState"/> via
/// <see cref="AggregateExplorer{TState}"/>, projects <see cref="UiStateGraphSnapshot"/> graphs from <see cref="IUiIntrospector"/>,
/// and synthesizes 3D <see cref="BrainSceneNode"/> state/trigger/guard graphs with <c>0–100%</c> scenario pass-rate rings from runtime Synapse units.
/// </summary>
public static partial class CreatorStateSpaceInspector
{
    private static readonly Lazy<StateGraph<CreatorWorkspaceState>> CachedCreatorGraph = new(() =>
        AggregateExplorer<CreatorWorkspaceState>.Explore(maxStates: 24));

    public static StateGraph<CreatorWorkspaceState> CreatorGraph => CachedCreatorGraph.Value;

    public static UnitStateSpaceSummary AnalyzeUnitSource(string alias, IReadOnlyDictionary<string, string> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentNullException.ThrowIfNull(files);

        var csText = string.Join("\n\n", files
            .Where(kv => kv.Key.EndsWith(".cs", StringComparison.Ordinal))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Value));

        if (string.IsNullOrWhiteSpace(csText))
        {
            return new UnitStateSpaceSummary(alias, null, [], [], []);
        }

        string? stateType = null;
        if (StateRecordRegex().Match(csText) is { Success: true } stateMatch)
        {
            stateType = stateMatch.Groups["type"].Value;
        }
        else if (NeuronStateBaseRegex().Match(csText) is { Success: true } neuronStateMatch)
        {
            stateType = neuronStateMatch.Groups["type"].Value;
        }
        else if (AnyRecordRegex().Match(csText) is { Success: true } recMatch)
        {
            stateType = recMatch.Groups["type"].Value;
        }

        var triggers = new List<string>();
        var commands = new List<string>();

        foreach (Match m in SynapseReactionRegex().Matches(csText))
        {
            var sig = m.Groups["signal"].Value.Trim();
            var method = m.Groups["method"].Value.Trim();
            var ret = m.Groups["ret"].Value.Trim();
            if (string.Equals(ret, "View", StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(sig))
            {
                triggers.Add(sig);
            }

            commands.Add($"{method} ({sig})");
        }

        foreach (Match m in OnHandlerRegex().Matches(csText))
        {
            var sig = m.Groups["signal"].Value.Trim();
            var method = m.Groups["method"].Value.Trim();
            if (!string.IsNullOrEmpty(sig))
            {
                triggers.Add(sig);
            }

            commands.Add($"{method} ({sig})");
        }

        foreach (Match m in FollowSubscriptionRegex().Matches(csText))
        {
            var sig = m.Groups["signal"].Value.Trim();
            if (!string.IsNullOrEmpty(sig))
            {
                triggers.Add(sig);
            }
        }

        foreach (Match m in DecisionMethodRegex().Matches(csText))
        {
            commands.Add(m.Groups["method"].Value.Trim());
        }

        var capabilities = new List<string>();
        var candidates = new List<string>();

        if (csText.Contains("ReadIssue", StringComparison.Ordinal))
        {
            capabilities.Add("ReadIssue");
        }

        if (csText.Contains("CommentOnIssue", StringComparison.Ordinal))
        {
            capabilities.Add("CommentOnIssue");
        }

        if (csText.Contains("PreviewIssueComment", StringComparison.Ordinal))
        {
            if (csText.Contains("CommentOnIssue", StringComparison.Ordinal))
            {
                candidates.Add("PreviewIssueComment (DraftForApproval)");
            }
            else
            {
                capabilities.Add("PreviewIssueComment");
            }
        }
        else if (csText.Contains("CommentOnIssue", StringComparison.Ordinal)
            && csText.Contains("IssueChanged", StringComparison.Ordinal))
        {
            candidates.Add("PreviewIssueComment (DraftForApproval)");
        }

        var rules = new List<string>();
        foreach (Match m in RuleNameRegex().Matches(csText))
        {
            rules.Add(m.Groups["rule"].Value.Trim());
        }

        foreach (Match m in PolicyGuardRegex().Matches(csText))
        {
            var policy = m.Groups["policy"].Value.Trim();
            rules.Add("policy_guard");
            if (!string.IsNullOrEmpty(policy))
            {
                rules.Add($"policy:{policy}");
            }
        }

        var isGitHubIssueTriage = csText.Contains("IssueChanged", StringComparison.Ordinal)
            || csText.Contains("github.issue-changed", StringComparison.Ordinal)
            || csText.Contains("ReadIssue", StringComparison.Ordinal)
            || csText.Contains("CommentOnIssue", StringComparison.Ordinal)
            || csText.Contains("PreviewIssueComment", StringComparison.Ordinal);

        if (isGitHubIssueTriage)
        {
            if (!rules.Contains("policy_guard", StringComparer.Ordinal))
            {
                rules.Add("policy_guard");
            }

            if (!rules.Contains("untrusted_evidence", StringComparer.Ordinal))
            {
                rules.Add("untrusted_evidence");
            }
        }

        var events = EmittedSignalRegex().Matches(csText)
            .Select(m => m.Groups["signal"].Value.Trim())
            .Concat(PublishedSignalNewRegex().Matches(csText).Select(m => m.Groups["signal"].Value.Trim()))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new UnitStateSpaceSummary(
            alias,
            stateType,
            [.. commands.Distinct(StringComparer.Ordinal)],
            [.. rules.Distinct(StringComparer.Ordinal)],
            events)
        {
            Triggers = [.. triggers.Distinct(StringComparer.Ordinal)],
            Capabilities = [.. capabilities.Distinct(StringComparer.Ordinal)],
            Candidates = [.. candidates.Distinct(StringComparer.Ordinal)],
        };
    }

    public static double? ComputeTranscriptPassRate(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return null;
        }

        var passed = 0;
        var failed = 0;
        foreach (var rawLine in transcript.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawLine.StartsWith('✓'))
            {
                passed++;
            }
            else if (rawLine.StartsWith('✗') || rawLine.StartsWith('✕'))
            {
                failed++;
            }
        }

        if (passed + failed == 0)
        {
            if (transcript.Contains("did not run", StringComparison.OrdinalIgnoreCase)
                || transcript.Contains("not a loaded unit", StringComparison.OrdinalIgnoreCase))
            {
                return 0.0;
            }

            return null;
        }

        return Math.Clamp((double)passed / (passed + failed), 0.0, 1.0);
    }

    public static int ResolvePassPercent(Inspection inspection, string? transcript = null, FlowView? activeFlow = null)
    {
        ArgumentNullException.ThrowIfNull(inspection);

        if (ComputeTranscriptPassRate(transcript) is { } transcriptRate)
        {
            return (int)Math.Round(transcriptRate * 100.0);
        }

        if (inspection.ScenarioPassRate is { } explicitRate)
        {
            return (int)Math.Round(Math.Clamp(explicitRate, 0.0, 1.0) * 100.0);
        }

        if (activeFlow is not null)
        {
            return activeFlow.Status switch
            {
                FlowStatus.Completed => 100,
                FlowStatus.Failed or FlowStatus.Cancelled => 0,
                _ => (int)Math.Round(Math.Clamp(activeFlow.Fraction, 0.0, 1.0) * 100.0),
            };
        }

        if (inspection.IntrospectedGraph is { } introspected)
        {
            return introspected.IsHealthy ? 100 : 0;
        }

        if (inspection.DeployedUnit is { } deployed)
        {
            if (deployed.Status == DeployStatus.Failed) { return 0; }
            if (deployed.Status == DeployStatus.Staged) { return 75; }
            if (!string.IsNullOrWhiteSpace(inspection.FailureSummary)
                && !inspection.FailureSummary.Contains("no recorded failures", StringComparison.OrdinalIgnoreCase))
            {
                return 50;
            }

            return 100;
        }

        if (inspection.Synapse is { } syn)
        {
            if (string.Equals(syn.LastRun, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                return syn.Runs > 1 ? 50 : 0;
            }

            if (string.Equals(syn.LastRun, "Succeeded", StringComparison.OrdinalIgnoreCase))
            {
                return 100;
            }

            return syn.Runs > 0 ? 75 : 100;
        }

        if (inspection.Files.Length > 0)
        {
            return inspection.Id.StartsWith("draft:", StringComparison.Ordinal) ? 50 : 100;
        }

        return 100;
    }

    public static bool HasInspectableUnitOrSynapse(
        CreatorWorkspaceState state,
        Inspection inspection,
        FlowView[] flows)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(flows);

        if (state.Selected is { Kind: Selection.Node })
        {
            if (inspection.Files.Length > 0
                || inspection.Synapse is not null
                || inspection.DeployedUnit is not null
                || inspection.IntrospectedGraph is { States.Length: > 0 })
            {
                return true;
            }
        }

        return state.ActiveRunId is { Length: > 0 } runId
            && flows.Any(f => string.Equals(f.FlowId, runId, StringComparison.Ordinal));
    }

    public static bool ShouldProjectLiveSynapseScene(
        CreatorWorkspaceState state,
        Inspection inspection,
        FlowView[] flows)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(flows);

        if (state.Selected is { Kind: Selection.Node } sel)
        {
            if (sel.Id.StartsWith(Selection.SynapsePrefix, StringComparison.Ordinal))
            {
                return true;
            }

            if (inspection.Synapse is not null
                || inspection.DeployedUnit is not null
                || inspection.IntrospectedGraph is { States.Length: > 0 }
                || inspection.Files.Length > 0)
            {
                return true;
            }
        }

        return state.Selected is null
            && state.ActiveRunId is { Length: > 0 } runId
            && flows.Any(f => string.Equals(f.FlowId, runId, StringComparison.Ordinal));
    }

    public static Card RenderUnitSummaryCard(string alias, IReadOnlyDictionary<string, string> files)
        => RenderUnitSummaryCard(alias, files, null, null);

    public static Card RenderUnitSummaryCard(
        string alias,
        IReadOnlyDictionary<string, string> files,
        Inspection? inspection,
        string? transcript = null)
    {
        var summary = AnalyzeUnitSource(alias, files);
        var creatorGraph = CreatorGraph;
        var passPct = inspection is null ? 100 : ResolvePassPercent(inspection, transcript);

        var subtitle = summary.StateType is { Length: > 0 } st
            ? string.Create(CultureInfo.InvariantCulture, $"State: {st} · {summary.Commands.Count} transition(s) · {summary.Rules.Count} guard rule(s) · {passPct}% pass")
            : summary.Commands.Count > 0 || summary.Triggers.Count > 0
                ? string.Create(CultureInfo.InvariantCulture, $"Synapse: {alias} · {summary.Triggers.Count} trigger(s) · {summary.Commands.Count} reaction(s) · {passPct}% pass")
                : string.Create(CultureInfo.InvariantCulture, $"Creator Aggregate: {creatorGraph.States.Count} reachable state(s), {creatorGraph.AcceptedTransitions.Count} accepted / {creatorGraph.RejectedTransitions.Count} refused edge(s)");

        var pills = new List<Node>
        {
            StatusPill(
                passPct >= 100 && creatorGraph.IsHealthy ? StatusKind.Done : (passPct == 0 ? StatusKind.Error : StatusKind.Working),
                creatorGraph.IsHealthy ? string.Create(CultureInfo.InvariantCulture, $"Invariants Healthy · {passPct}%") : $"{creatorGraph.Violations.Count} Violation(s)"),
        };

        if (summary.Triggers.Count > 0)
        {
            pills.Add(StatusPill(StatusKind.Working, $"Triggers: {string.Join(", ", summary.Triggers)}"));
        }

        if (summary.Commands.Count > 0)
        {
            pills.Add(StatusPill(StatusKind.Idle, $"{summary.Commands.Count} Command(s)"));
        }

        if (summary.Rules.Count > 0)
        {
            pills.Add(StatusPill(StatusKind.NeedsYou, $"Rules: {string.Join(", ", summary.Rules)}"));
        }

        return (Card)Card("Aggregate State Graph", subtitle)[
            HStack(Space.Sm, [.. pills]),
            summary.Commands.Count > 0
                ? VStack(Space.Xs, [.. summary.Commands.Select(cmd =>
                    MutedText($"{summary.StateType ?? alias} → {cmd} ⇒ {(summary.Events.Count > 0 ? string.Join(", ", summary.Events) : "Store(next)")}"))])
                : MutedText($"Unfolded {creatorGraph.States.Count} reachable state(s), {creatorGraph.AcceptedTransitions.Count} accepted & {creatorGraph.RejectedTransitions.Count} rule-refused self-loop transition(s).")
        ].ToNode();
    }

    public static BrainSceneNode RenderUnitStateScene(string alias, IReadOnlyDictionary<string, string> files)
        => RenderUnitStateScene(alias, files, passPct: 100, synapse: null);

    public static BrainSceneNode RenderInspectionScene(
        CreatorWorkspaceState state,
        Inspection inspection,
        FlowView[] flows)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(flows);

        var activeFlow = state.ActiveRunId is { Length: > 0 } runId
            ? flows.FirstOrDefault(f => string.Equals(f.FlowId, runId, StringComparison.Ordinal))
            : null;
        var passPct = ResolvePassPercent(inspection, state.Transcript, activeFlow);

        if (inspection.Files.Length > 0 && inspection.Alias is { Length: > 0 } fileAlias)
        {
            return RenderUnitStateScene(fileAlias, inspection.FileMap(), passPct, inspection.Synapse);
        }

        if (inspection.IntrospectedGraph is { States.Length: > 0 } introspected)
        {
            return RenderIntrospectedGraphScene(introspected, passPct);
        }

        if (inspection.Synapse is { Form: SynapseForm.Flow or SynapseForm.Workflow } flowOrWorkflow)
        {
            var matchedFlow = activeFlow
                ?? flows.FirstOrDefault(f => string.Equals(f.Type, flowOrWorkflow.Name, StringComparison.Ordinal)
                    || string.Equals(f.Subject, flowOrWorkflow.Name, StringComparison.Ordinal));
            return RenderWorkflowOrFlowScene(flowOrWorkflow.Name, flowOrWorkflow, matchedFlow, passPct);
        }

        if (activeFlow is not null && state.Selected is null)
        {
            return RenderWorkflowOrFlowScene(activeFlow.Title, null, activeFlow, passPct);
        }

        if (inspection.Synapse is { } syn)
        {
            return RenderCatalogSynapseScene(syn, passPct);
        }

        if (inspection.Alias is { Length: > 0 } unitAlias && inspection.DeployedUnit is not null)
        {
            return RenderCatalogSynapseScene(
                new SynapseDefinition($"reaction:{unitAlias}", SynapseForm.Reaction, unitAlias, "Deployed Synapse unit", 1, "Succeeded"),
                passPct);
        }

        return StateGraphView(CreatorGraph);
    }

    public static BrainSceneNode RenderUnitStateScene(
        string alias,
        IReadOnlyDictionary<string, string> files,
        int passPct,
        SynapseDefinition? synapse = null)
    {
        var clampedPct = Math.Clamp(passPct, 0, 100);
        var summary = AnalyzeUnitSource(alias, files);
        if (summary.Commands.Count == 0
            && summary.Triggers.Count == 0
            && summary.Capabilities.Count == 0
            && summary.StateType is null)
        {
            return synapse is not null
                ? RenderCatalogSynapseScene(synapse, clampedPct)
                : StateGraphView(CreatorGraph);
        }

        var coreStatus = clampedPct == 0 ? "error" : (clampedPct >= 100 ? "done" : "working");
        var stateLabel = summary.StateType ?? synapse?.Name ?? alias;
        var stateNodeId = $"state:{stateLabel}";
        var nodes = new List<BrainGraphNode>();
        var synapses = new List<BrainGraphSynapse>();

        // 1. Trigger Signal Nodes (left column X = -250) -> accepted path into State/Reaction hub
        for (var t = 0; t < summary.Triggers.Count; t++)
        {
            var trigger = summary.Triggers[t];
            var triggerId = $"trigger:{t}:{trigger}";
            var trigY = (t - ((summary.Triggers.Count - 1) / 2.0)) * 112.0;
            nodes.Add(new BrainGraphNode(
                Id: triggerId,
                Label: trigger,
                Uri: $"signal://{alias}/{trigger}",
                Kind: "unit",
                X: -250,
                Y: trigY,
                Z: 0,
                Status: string.Create(CultureInfo.InvariantCulture, $"active · {clampedPct}%"),
                Subtitle: "Trigger Signal"));
            synapses.Add(new BrainGraphSynapse(triggerId, stateNodeId, trigger, Active: true));
        }

        // 2. Central State / Reaction Unit Hub (X = 0) with 0–100% scenario pass-rate ring
        var opTotal = summary.Commands.Count + summary.Capabilities.Count;
        nodes.Add(new BrainGraphNode(
            Id: stateNodeId,
            Label: stateLabel,
            Uri: $"state://{alias}/{stateNodeId}",
            Kind: "initial",
            X: 0,
            Y: 0,
            Z: 0,
            Status: string.Create(CultureInfo.InvariantCulture, $"{coreStatus} · {clampedPct}%"),
            Subtitle: string.Create(CultureInfo.InvariantCulture, $"{opTotal} transition(s) · {clampedPct}% pass")));

        // 3. Accepted Operation / Capability Nodes + Candidate Branches (right column X = 250)
        var operations = summary.Commands
            .Concat(summary.Capabilities.Where(cap => !summary.Commands.Any(c => c.Contains(cap, StringComparison.Ordinal))))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var totalRightNodes = operations.Count + summary.Candidates.Count;
        string? readIssueNodeId = null;
        string? commentNodeId = null;

        for (var i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            var nextId = $"next:{i}:{op}";
            var centeredY = (i - ((Math.Max(1, totalRightNodes) - 1) / 2.0)) * 108.0;
            var isVerifyLike = op.Contains("Verify", StringComparison.OrdinalIgnoreCase)
                || op.Contains("ReadIssue", StringComparison.Ordinal);
            nodes.Add(new BrainGraphNode(
                Id: nextId,
                Label: op,
                Uri: $"state://{alias}/{nextId}",
                Kind: isVerifyLike ? "verify" : "state",
                X: 250,
                Y: centeredY,
                Z: 0,
                Status: string.Create(CultureInfo.InvariantCulture, $"{coreStatus} · {clampedPct}%"),
                Subtitle: summary.Events.Count > 0 ? string.Join(", ", summary.Events) : "Accepted path"));
            synapses.Add(new BrainGraphSynapse(stateNodeId, nextId, op, Active: true));

            if (op.Contains("ReadIssue", StringComparison.Ordinal))
            {
                readIssueNodeId = nextId;
            }

            if (op.Contains("CommentOnIssue", StringComparison.Ordinal))
            {
                commentNodeId = nextId;
            }
        }

        for (var c = 0; c < summary.Candidates.Count; c++)
        {
            var cand = summary.Candidates[c];
            var candIdx = operations.Count + c;
            var candId = $"candidate:{c}:{cand}";
            var centeredY = (candIdx - ((Math.Max(1, totalRightNodes) - 1) / 2.0)) * 108.0;
            nodes.Add(new BrainGraphNode(
                Id: candId,
                Label: cand,
                Uri: $"state://{alias}/{candId}",
                Kind: "worktree",
                X: 250,
                Y: centeredY,
                Z: 0,
                Status: string.Create(CultureInfo.InvariantCulture, $"standby · {clampedPct}%"),
                Subtitle: "Candidate approval path"));
            synapses.Add(new BrainGraphSynapse(stateNodeId, candId, $"candidate:{cand}", Active: false));
        }

        // 4. Policy & Domain Rule Guard Edges (`refused` `✕ <rule>`)
        foreach (var rule in summary.Rules)
        {
            var guardHostId = string.Equals(rule, "untrusted_evidence", StringComparison.Ordinal) && readIssueNodeId is not null
                ? readIssueNodeId
                : (rule.StartsWith("policy", StringComparison.Ordinal) && commentNodeId is not null
                    ? commentNodeId
                    : stateNodeId);
            synapses.Add(new BrainGraphSynapse(guardHostId, guardHostId, $"Guard [refused: {rule}]", Active: false));
        }

        return new BrainSceneNode(nodes, synapses, stateNodeId)
        {
            PauseRotation = true,
        };
    }

    public static BrainSceneNode RenderIntrospectedGraphScene(UiStateGraphSnapshot snapshot, int passPct = 100)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var clampedPct = Math.Clamp(passPct, 0, 100);
        const int maxRowsPerSubColumn = 4;
        const double xStep = 230.0;
        const double yStep = 124.0;

        var nodes = new List<BrainGraphNode>(snapshot.States.Length);
        var byDepth = snapshot.States.GroupBy(s => s.Depth).OrderBy(g => g.Key).ToList();

        var currentBaseX = 0.0;
        foreach (var depthGroup in byDepth)
        {
            var siblings = depthGroup.ToList();
            var subColumnCount = Math.Max(1, (int)Math.Ceiling(siblings.Count / (double)maxRowsPerSubColumn));
            for (var idx = 0; idx < siblings.Count; idx++)
            {
                var st = siblings[idx];
                var subCol = idx / maxRowsPerSubColumn;
                var subRow = idx % maxRowsPerSubColumn;
                var rowsInSubCol = Math.Min(maxRowsPerSubColumn, siblings.Count - (subCol * maxRowsPerSubColumn));
                var centeredY = (subRow - ((rowsInSubCol - 1) / 2.0)) * yStep;
                var x = currentBaseX + (subCol * xStep);
                var isInitial = st.Depth == 0 && idx == 0;
                var status = !snapshot.IsHealthy
                    ? $"error · {clampedPct}%"
                    : isInitial
                        ? string.Create(CultureInfo.InvariantCulture, $"done · {clampedPct}%")
                        : string.Create(CultureInfo.InvariantCulture, $"active · {clampedPct}%");

                nodes.Add(new BrainGraphNode(
                    Id: st.Id,
                    Label: st.Label,
                    Uri: $"state://{snapshot.Name}/{st.Id}",
                    Kind: isInitial ? "initial" : "state",
                    X: x,
                    Y: centeredY,
                    Z: 0,
                    Status: status,
                    Subtitle: string.Create(CultureInfo.InvariantCulture, $"depth {st.Depth} · {clampedPct}%")));
            }

            currentBaseX += subColumnCount * xStep;
        }

        var synapses = new List<BrainGraphSynapse>(snapshot.Transitions.Length);
        foreach (var edge in snapshot.Transitions)
        {
            var label = edge.Accepted
                ? (edge.Signals.Length > 0 ? $"{edge.Command} -> {string.Join(", ", edge.Signals)}" : edge.Command)
                : $"{edge.Command} [refused: {(edge.BrokenRules.Length > 0 ? string.Join(", ", edge.BrokenRules) : "guard_rule")}]";
            synapses.Add(new BrainGraphSynapse(edge.FromId, edge.ToId, label, Active: edge.Accepted));
        }

        return new BrainSceneNode(nodes, synapses, snapshot.States.FirstOrDefault()?.Id)
        {
            PauseRotation = true,
        };
    }

    private static BrainSceneNode RenderWorkflowOrFlowScene(
        string name,
        SynapseDefinition? definition,
        FlowView? flow,
        int passPct)
    {
        var clampedPct = Math.Clamp(passPct, 0, 100);
        var steps = flow is { Steps.Length: > 0 }
            ? flow.Steps
            : ["trigger", "execute", "approval-gate", "complete"];
        var doneSet = flow?.Done.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        var nodes = new List<BrainGraphNode>(steps.Length);
        var synapses = new List<BrainGraphSynapse>();

        for (var i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            var stepId = $"step:{i}:{step}";
            var x = (i - ((steps.Length - 1) / 2.0)) * 220.0;
            var isDone = flow is null
                ? (definition?.Runs > 0 && string.Equals(definition.LastRun, "Succeeded", StringComparison.OrdinalIgnoreCase))
                : doneSet.Contains(step);
            var isCurrent = flow is not null && string.Equals(flow.Current, step, StringComparison.Ordinal);
            var stepPct = isDone ? 100 : (isCurrent ? Math.Max(25, clampedPct) : (flow is null ? clampedPct : 0));
            var status = isDone
                ? "done · 100%"
                : isCurrent
                    ? string.Create(CultureInfo.InvariantCulture, $"working · {stepPct}%")
                    : string.Create(CultureInfo.InvariantCulture, $"idle · {stepPct}%");
            var kind = i == 0
                ? "initial"
                : step.Contains("verify", StringComparison.OrdinalIgnoreCase)
                    ? "verify"
                    : step.Contains("pr", StringComparison.OrdinalIgnoreCase)
                        ? "pr"
                        : "slice";

            nodes.Add(new BrainGraphNode(
                Id: stepId,
                Label: step,
                Uri: $"flow://{name}/{step}",
                Kind: kind,
                X: x,
                Y: 0,
                Z: 0,
                Status: status,
                Subtitle: flow?.Title ?? $"{definition?.Form.ToString() ?? "Flow"} · {name}"));

            if (i > 0)
            {
                var prevId = $"step:{i - 1}:{steps[i - 1]}";
                var activeEdge = isDone || isCurrent || (flow is null && i == 1);
                var label = activeEdge ? $"advance:{step}" : $"candidate:{step}";
                synapses.Add(new BrainGraphSynapse(prevId, stepId, label, Active: activeEdge));
            }

            if (step.Contains("approv", StringComparison.OrdinalIgnoreCase)
                || step.Contains("install", StringComparison.OrdinalIgnoreCase)
                || (flow is not null && string.Equals(flow.WaitingStep, step, StringComparison.Ordinal)))
            {
                synapses.Add(new BrainGraphSynapse(stepId, stepId, $"{step} [refused: person_confirmation_required]", Active: false));
            }
        }

        var selectedId = flow?.Current is { Length: > 0 } cur
            ? nodes.FirstOrDefault(n => string.Equals(n.Label, cur, StringComparison.Ordinal))?.Id ?? nodes[0].Id
            : nodes[0].Id;

        return new BrainSceneNode(nodes, synapses, selectedId)
        {
            PauseRotation = true,
        };
    }

    private static BrainSceneNode RenderCatalogSynapseScene(SynapseDefinition synapse, int passPct)
    {
        var clampedPct = Math.Clamp(passPct, 0, 100);
        var triggerId = $"trigger:{synapse.Id}";
        var hubId = $"synapse-hub:{synapse.Id}";
        var actionId = $"action:{synapse.Id}";
        var candidateId = $"candidate:{synapse.Id}";

        var nodes = new List<BrainGraphNode>
        {
            new(
                Id: triggerId,
                Label: synapse.Form == SynapseForm.App ? "Command / Tick" : "Signal Trigger",
                Uri: $"signal://{synapse.Name}/trigger",
                Kind: "unit",
                X: -240,
                Y: 0,
                Z: 0,
                Status: string.Create(CultureInfo.InvariantCulture, $"active · {clampedPct}%"),
                Subtitle: $"{synapse.Form} Ingress"),
            new(
                Id: hubId,
                Label: synapse.Name,
                Uri: $"synapse://{synapse.Id}",
                Kind: synapse.Form == SynapseForm.App ? "app" : "initial",
                X: 0,
                Y: 0,
                Z: 0,
                Status: string.Create(CultureInfo.InvariantCulture, $"{(clampedPct == 0 ? "error" : "done")} · {clampedPct}%"),
                Subtitle: string.IsNullOrWhiteSpace(synapse.Description)
                    ? string.Create(CultureInfo.InvariantCulture, $"{synapse.Form} · {synapse.Runs} run(s)")
                    : synapse.Description),
            new(
                Id: actionId,
                Label: synapse.Form == SynapseForm.App ? "Publish View & State" : "Commit Reaction",
                Uri: $"synapse://{synapse.Id}/commit",
                Kind: "verify",
                X: 240,
                Y: -56,
                Z: 0,
                Status: string.Create(CultureInfo.InvariantCulture, $"done · {clampedPct}%"),
                Subtitle: synapse.LastRun ?? "Accepted path"),
            new(
                Id: candidateId,
                Label: "Draft / Dry-Run",
                Uri: $"synapse://{synapse.Id}/draft",
                Kind: "worktree",
                X: 240,
                Y: 56,
                Z: 0,
                Status: string.Create(CultureInfo.InvariantCulture, $"standby · {clampedPct}%"),
                Subtitle: "Candidate preview"),
        };

        var synapses = new List<BrainGraphSynapse>
        {
            new(triggerId, hubId, $"wake:{synapse.Name}", Active: true),
            new(hubId, actionId, $"apply:{synapse.Name}", Active: true),
            new(hubId, candidateId, $"candidate:preview-{synapse.Name}", Active: false),
            new(hubId, hubId, $"{synapse.Name} [refused: policy_guard]", Active: false),
        };

        return new BrainSceneNode(nodes, synapses, hubId)
        {
            PauseRotation = true,
        };
    }

    [GeneratedRegex(@"\[State\([^\)]*\)\]\s*public\s+(?:sealed\s+)?record\s+(?<type>[A-Za-z0-9_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex StateRecordRegex();

    [GeneratedRegex(@"class\s+[A-Za-z0-9_]+\s*:\s*(?:Neuron|AggregateScreen|ConnectionNeuron)<\s*(?<type>[A-Za-z0-9_]+)\s*>", RegexOptions.CultureInvariant)]
    private static partial Regex NeuronStateBaseRegex();

    [GeneratedRegex(@"public\s+sealed\s+record\s+(?<type>[A-Za-z0-9_]*State)\b", RegexOptions.CultureInvariant)]
    private static partial Regex AnyRecordRegex();

    [GeneratedRegex(@"\[Synapse\(\s*(?:typeof\((?<signal>[A-Za-z0-9_.]+)\)|""(?<signal>[^""]+)"")[^\]]*\)\]\s*public\s+static\s+(?:async\s+)?(?<ret>[^\s\(]+)\s+(?<method>[A-Za-z0-9_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SynapseReactionRegex();

    [GeneratedRegex(@"\[(?:DigitalBrain\.Neurons\.)?On\]\s*public\s+(?:async\s+)?[^\s\(]+\s+(?<method>[A-Za-z0-9_]+)\s*\(\s*(?<signal>[A-Za-z0-9_]+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex OnHandlerRegex();

    [GeneratedRegex(@"Follow<\s*[^,>]+,\s*(?<signal>[A-Za-z0-9_]+)\s*>", RegexOptions.CultureInvariant)]
    private static partial Regex FollowSubscriptionRegex();

    [GeneratedRegex(@"public\s+Decision<[^>]+>\s+(?<method>[A-Za-z0-9_]+)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex DecisionMethodRegex();

    [GeneratedRegex(@"Decision\.Rule\(\s*""(?<rule>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex RuleNameRegex();

    [GeneratedRegex(@"Policies\.Guard\(\s*""(?<policy>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex PolicyGuardRegex();

    [GeneratedRegex(@"\[Signal\(\s*""(?<signal>[^""]+)""\s*\)\]", RegexOptions.CultureInvariant)]
    private static partial Regex EmittedSignalRegex();

    [GeneratedRegex(@"(?:PublishAsync|Reaction<[^>]+>\.Of)\([^\)]*new\s+(?<signal>[A-Za-z0-9_]+)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex PublishedSignalNewRegex();
}

public sealed record UnitStateSpaceSummary(
    string Alias,
    string? StateType,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> Rules,
    IReadOnlyList<string> Events)
{
    public IReadOnlyList<string> Triggers { get; init; } = [];
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public IReadOnlyList<string> Candidates { get; init; } = [];
}
