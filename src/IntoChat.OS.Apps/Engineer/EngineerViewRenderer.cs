using System.Globalization;
using DigitalBrain;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Engineer;

internal static class EngineerViewRenderer
{
    public const string AppOwner = "app.engineer";

    // What the Engineer view needs from its screen to draw a control child: only the screen can render one. A view drawn without the
    // screen (a static render) has no children and leaves them out.
    public sealed record Children(
        Func<string, TabStripProps, Node> TabStrip,
        Func<string, TreeProps, Node> Tree,
        Func<string, ChatThreadProps, Node> ChatThread,
        Func<string, QueryTableProps, Node> Table);

    public static Node RenderRoot(EngineerWorkspaceState state, Rejected? lastRejection, string transition = "", BrainId? brain = null, Children? children = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var activeTab = EngineerWorkspaceState.NormalizeTab(state.ActiveTab);
        var toolbar = RenderTopBar(state, children);
        var sidebar = RenderLeftColumn(state, lastRejection, children);
        var body = activeTab == "plan"
            ? RenderFlowStage(state, brain)
            : RenderChangesAndDiffStage(state);
        var inspector = RenderRightInspector(state, activeTab, children);

        return BindOwner(
            AppShell(
                key: "engineer-shell",
                sidebar: sidebar,
                body: body,
                toolbar: toolbar,
                inspector: inspector,
                automationName: "IntoChat Unified Studio",
                transition: transition),
            AppOwner);
    }

    private static Node RenderTopBar(EngineerWorkspaceState state, Children? children)
    {
        var winner = state.Candidates.ElementAtOrDefault(state.SelectedInstanceIndex)
            ?? state.Candidates.FirstOrDefault();
        var activeBranch = winner?.BranchName ?? $"ino/engineer-{state.ActiveRunId}";
        var harnessLabel = state.Harness switch
        {
            EngineeringHarnessKind.ClaudeCli => "Claude CLI",
            EngineeringHarnessKind.CodexCli => "Codex CLI",
            _ => "Ino Native",
        };
        var activeCollaborators = state.EffectiveCollaborators.Count(static c => c.Active);
        var presencePill = StatusPill(
            state.PrPublished ? StatusKind.Done : (state.PlanApproved ? StatusKind.Working : StatusKind.Idle),
            string.Create(CultureInfo.InvariantCulture, $"{harnessLabel} · {Math.Max(1, activeCollaborators)} Active"));
        var repoBranchPill = StatusPill(StatusKind.Idle, $"{state.RepositoryId} · {activeBranch}");

        var candidatePills = state.Candidates
            .Select(c =>
            {
                var isSelected = c.InstanceIndex == state.SelectedInstanceIndex;
                var label = isSelected
                    ? string.Create(CultureInfo.InvariantCulture, $"w{c.InstanceIndex + 1} ★")
                    : string.Create(CultureInfo.InvariantCulture, $"w{c.InstanceIndex + 1}");
                var btn = isSelected ? PrimaryButton(label) : GhostButton(label);
                return (Node)btn
                    .Key(EngineerApp.CandidateSelectKey(c.InstanceIndex))
                    .Small()
                    .Guard(state.ValidCandidateSelection(c.InstanceIndex))
                    .Sends(new EngineerSelectCandidate(c.InstanceIndex));
            })
            .ToArray();

        List<Node?> barItems =
        [
            presencePill,
            repoBranchPill,
            .. candidatePills,
            children?.TabStrip(EngineerApp.TabsKey, TabsOf(state)),
            new Button("engineer:open-aspire", "Aspire ↗", Intent: AppSignals.OpenApp("aspire", state.ActiveTraceId ?? state.ActiveFlowId ?? state.ActiveRunId))
            {
                Small = true,
                Icon = "activity",
            },
            new Button("engineer:open-orleans", "Orleans ↗", Intent: AppSignals.OpenApp("orleans", "EngineerFlow"))
            {
                Small = true,
                Icon = "cpu",
            },
            GhostButton("New Run", Icons.Plus)
                .Key("engineer:new-run")
                .Small()
                .Sends(new EngineerNewRun()),
            PrimaryButton("Ratify Plan", Icons.Check)
                .Key("engineer:approve-plan")
                .Small()
                .Guard(state.PlanReadyToApprove())
                .Sends(new EngineerApprovePlan()),
            Button("Run Verify", "play")
                .Key("engineer:run-verify")
                .Small()
                .Guard(state.ReadyForVerification())
                .Sends(new EngineerRunVerify()),
            PrimaryButton("Ship PR", "git-pull-request")
                .Key("engineer:approve-pr")
                .Small()
                .Guard(state.ReadyForPrApproval())
                .Sends(new EngineerApprovePr()),
        ];

        return HStack(Space.Xs, [.. barItems]);
    }

    private static Node RenderLeftColumn(EngineerWorkspaceState state, Rejected? lastRejection, Children? children)
    {
        var slices = state.EffectiveSlices;
        var cleanup = state.EffectiveCleanupTargets;

        var sliceProgressNodes = slices
            .Select(s => (Node)new DigitalBrain.UI.Progress(
                string.Create(CultureInfo.InvariantCulture, $"{s.Title} · {s.Percent}%"),
                Math.Clamp(s.Percent / 100.0, 0.0, 1.0)))
            .ToArray();

        var cleanupNodes = cleanup
            .Take(4)
            .Select(static path => (Node)MutedText($"[-] {Path.GetFileName(path)}"))
            .ToArray();

        Node?[] leftChildren =
        [
            lastRejection is not null
                ? Banner("Domain rule refused command", string.Join(" ", lastRejection.Messages), tone: "error")
                : null,
            SectionHeader("Mission Thread", state.Goal),
            children?.ChatThread(EngineerApp.BrainstormKey, BrainstormOf()),
            SectionHeader(
                "Ratified Plan & Ino Slices",
                string.Create(CultureInfo.InvariantCulture, $"{slices.Length} parallel slice(s) · {cleanup.Length} cleanup target(s)")),
            .. sliceProgressNodes,
            .. cleanupNodes,
            SectionHeader("Worktrees", $"{state.InstanceCount} lease(s) · {state.BackendMode}"),
            HStack(Space.Xs)[
                (state.InstanceCount == 1 ? PrimaryButton("1x") : GhostButton("1x"))
                    .Key("engineer:instances:1")
                    .Small()
                    .Sends(new EngineerSetInstances(1, state.BackendMode)),
                (state.InstanceCount == 2 ? PrimaryButton("2x") : GhostButton("2x"))
                    .Key("engineer:instances:2")
                    .Small()
                    .Sends(new EngineerSetInstances(2, state.BackendMode)),
                (state.InstanceCount == 3 ? PrimaryButton("3x") : GhostButton("3x"))
                    .Key("engineer:instances:3")
                    .Small()
                    .Sends(new EngineerSetInstances(3, state.BackendMode)),
                (state.BackendMode == "LocalGit" ? PrimaryButton("Local") : GhostButton("Local"))
                    .Key("engineer:backend:local")
                    .Small()
                    .Sends(new EngineerSetInstances(state.InstanceCount, "LocalGit")),
                (state.BackendMode == "ContainerVolume" ? PrimaryButton("Cloud") : GhostButton("Cloud"))
                    .Key("engineer:backend:volume")
                    .Small()
                    .Sends(new EngineerSetInstances(state.InstanceCount, "ContainerVolume"))
            ],
            children?.Tree(EngineerApp.WorktreesKey, WorktreesOf(state)),
        ];

        return VStack(Space.Sm, leftChildren);
    }

    private static Node RenderFlowStage(EngineerWorkspaceState state, BrainId? brain)
    {
        var graph = BuildStudioExecutionGraph(state);
        if (!state.LiveViewportPeek)
        {
            // Return BrainSceneNode directly so AppShellNode + NodeRenderer.fills stretches it to 100% height!
            return graph;
        }

        var peekUri = brain is { } activeBrain
            ? new NeuronId(activeBrain, "apps", "ino/view").ToString()
            : "apps/ino/view";
        var peekPane = StudioFrame(
            "Live App Viewport Peek",
            $"Mounted via Slot(\"{peekUri}\")",
            null,
            Slot(peekUri, "app"));
        return Split(graph, peekPane, leadingExtent: 560, gap: Space.Sm);
    }

    private static BrainSceneNode BuildStudioExecutionGraph(EngineerWorkspaceState state)
    {
        var nodes = new List<BrainGraphNode>();
        var synapses = new List<BrainGraphSynapse>();

        var planPct = state.PlanApproved ? 100 : (state.HasPlan ? 50 : 15);
        var verifyPct = state.VerificationPassed ? 100 : (state.CurrentStage is "build-and-test" or "live-verify" ? 65 : 0);
        var prPct = state.PrPublished ? 100 : (state.VerificationPassed ? 90 : 0);

        nodes.Add(new BrainGraphNode(
            Id: "discuss",
            Label: "Discuss & Hydrate",
            Uri: "state://engineer/discuss",
            Kind: "initial",
            X: -420,
            Y: 0,
            Z: 0,
            Status: "done · 100%",
            Subtitle: "WorkingMemory · UiIntrospector"));

        nodes.Add(new BrainGraphNode(
            Id: "ratify-plan",
            Label: "Ratify Plan",
            Uri: "state://engineer/ratify-plan",
            Kind: "state",
            X: -190,
            Y: 0,
            Z: 0,
            Status: state.PlanApproved ? "done · 100%" : $"working · {planPct}%",
            Subtitle: string.Create(CultureInfo.InvariantCulture, $"{state.EffectiveSlices.Length} slices · -{state.EffectiveCleanupTargets.Length} trash")));

        synapses.Add(new BrainGraphSynapse("discuss", "ratify-plan", "HydrateBrain", Active: true));
        synapses.Add(new BrainGraphSynapse("ratify-plan", "ratify-plan", "RatifyPlan [rejected: overlapping_file_slices]", Active: false));

        var slices = state.EffectiveSlices;
        for (var i = 0; i < slices.Length; i++)
        {
            var s = slices[i];
            var sliceY = (i - ((slices.Length - 1) / 2.0)) * 116.0;
            var sliceNodeId = $"slice:{s.SliceId}";
            nodes.Add(new BrainGraphNode(
                Id: sliceNodeId,
                Label: s.Title,
                Uri: $"state://engineer/{sliceNodeId}",
                Kind: "slice",
                X: 50,
                Y: sliceY,
                Z: 0,
                Status: string.Create(CultureInfo.InvariantCulture, $"{s.Status} · {s.Percent}%"),
                Subtitle: s.AssignedAgentKey));
            synapses.Add(new BrainGraphSynapse("ratify-plan", sliceNodeId, $"Dispatch:{s.SliceId}", Active: state.PlanApproved));
        }

        var candidates = state.Candidates;
        for (var i = 0; i < candidates.Length; i++)
        {
            var c = candidates[i];
            var candY = (i - ((candidates.Length - 1) / 2.0)) * 124.0;
            var candNodeId = string.Create(CultureInfo.InvariantCulture, $"wt-{c.InstanceIndex}");
            var isSelected = c.InstanceIndex == state.SelectedInstanceIndex;
            var candPct = state.PrPublished && isSelected
                ? 100
                : state.VerificationPassed
                    ? 100
                    : state.PlanApproved ? 75 : 20;
            nodes.Add(new BrainGraphNode(
                Id: candNodeId,
                Label: string.Create(CultureInfo.InvariantCulture, $"w{c.InstanceIndex + 1} · {c.StrategyLabel}"),
                Uri: $"state://engineer/{candNodeId}",
                Kind: "worktree",
                X: 300,
                Y: candY,
                Z: 0,
                Status: string.Create(CultureInfo.InvariantCulture, $"{c.Status} · {candPct}%"),
                Subtitle: c.BranchName,
                Select: (DigitalBrain.UI.Button)(isSelected ? PrimaryButton($"w{c.InstanceIndex + 1}") : Button($"w{c.InstanceIndex + 1}"))
                    .Key($"graph:wt:{c.InstanceIndex}")
                    .Sends(new EngineerSelectCandidate(c.InstanceIndex))
                    .ToNode()));

            foreach (var s in slices)
            {
                synapses.Add(new BrainGraphSynapse(
                    $"slice:{s.SliceId}",
                    candNodeId,
                    isSelected ? $"converge:w{c.InstanceIndex + 1}" : $"candidate:w{c.InstanceIndex + 1}",
                    Active: state.PlanApproved && isSelected));
            }
        }

        nodes.Add(new BrainGraphNode(
            Id: "verify",
            Label: "Verify & Live Probe",
            Uri: "state://engineer/verify",
            Kind: "verify",
            X: 540,
            Y: 0,
            Z: 0,
            Status: state.VerificationPassed ? "verified · 100%" : $"pending · {verifyPct}%",
            Subtitle: "Roslyn · MSBuild · Playwright"));

        for (var i = 0; i < candidates.Length; i++)
        {
            var c = candidates[i];
            var isSelected = c.InstanceIndex == state.SelectedInstanceIndex;
            var candNodeId = string.Create(CultureInfo.InvariantCulture, $"wt-{c.InstanceIndex}");
            synapses.Add(new BrainGraphSynapse(
                candNodeId,
                "verify",
                isSelected ? "VerifyWinner" : $"candidate:verify-w{c.InstanceIndex + 1}",
                Active: state.VerificationPassed && isSelected));
        }

        var winner = candidates.ElementAtOrDefault(state.SelectedInstanceIndex);
        var shortSha = winner?.CommitSha is { Length: >= 8 } sha ? sha[..8] : "pending";
        nodes.Add(new BrainGraphNode(
            Id: "ship-pr",
            Label: "Ship Draft PR",
            Uri: "state://engineer/ship-pr",
            Kind: "pr",
            X: 770,
            Y: 0,
            Z: 0,
            Status: state.PrPublished ? "published · 100%" : $"gate · {prPct}%",
            Subtitle: state.PrPublished ? (state.PullRequestUrl ?? "Published") : $"SHA: {shortSha}"));

        synapses.Add(new BrainGraphSynapse("verify", "ship-pr", "MintShaConfirmation", Active: state.VerificationPassed));
        synapses.Add(new BrainGraphSynapse("ship-pr", "ship-pr", "ApprovePr [rejected: requires_verified_sha]", Active: false));

        var focusedNodeId = state.PrPublished || state.VerificationPassed
            ? "ship-pr"
            : state.PlanApproved
                ? string.Create(CultureInfo.InvariantCulture, $"wt-{state.SelectedInstanceIndex}")
                : "ratify-plan";

        return new BrainSceneNode(nodes, synapses, focusedNodeId)
        {
            PauseRotation = true,
        };
    }

    private static Node RenderChangesAndDiffStage(EngineerWorkspaceState state)
    {
        var activeCandidate = state.Candidates.ElementAtOrDefault(state.SelectedInstanceIndex)
            ?? state.Candidates.FirstOrDefault();
        var changedFiles = activeCandidate?.ChangedFiles ?? [];
        var deletedFiles = activeCandidate?.DeletedFiles ?? state.EffectiveCleanupTargets;
        var (totalAdd, totalDel) = EstimateLineCounts(activeCandidate?.UnifiedDiff, activeCandidate?.ChangedLines ?? 0, Math.Max(1, changedFiles.Length));

        var fileItems = new List<Node>
        {
            SectionHeader(
                "Changed Files",
                string.Create(CultureInfo.InvariantCulture, $"{changedFiles.Length + deletedFiles.Length} file(s) · +{totalAdd} / -{totalDel}")),
        };

        for (var i = 0; i < changedFiles.Length; i++)
        {
            var file = changedFiles[i];
            var isSelected = string.Equals(file, state.SelectedFilePath, StringComparison.Ordinal);
            var isNew = file.Contains("New", StringComparison.OrdinalIgnoreCase) || file.Contains("Journeys", StringComparison.Ordinal);
            var statusPrefix = isNew ? "[+]" : "[•]";
            var perFileAdd = Math.Max(1, totalAdd / Math.Max(1, changedFiles.Length));
            var perFileDel = totalDel / Math.Max(1, changedFiles.Length);
            var label = string.Create(CultureInfo.InvariantCulture, $"{statusPrefix} {Path.GetFileName(file)}");
            var badge = string.Create(CultureInfo.InvariantCulture, $"+{perFileAdd}/-{perFileDel}");
            var btn = isSelected ? PrimaryButton($"{label} ({badge})") : GhostButton($"{label} ({badge})");
            fileItems.Add(btn
                .Key(string.Create(CultureInfo.InvariantCulture, $"engineer:diff-file:{i}"))
                .Small()
                .Sends(new EngineerSelectFile(file)));
        }

        for (var i = 0; i < deletedFiles.Length; i++)
        {
            var del = deletedFiles[i];
            var label = $"[-] {Path.GetFileName(del)} (deleted)";
            fileItems.Add(GhostButton(label)
                .Key(string.Create(CultureInfo.InvariantCulture, $"engineer:deleted-file:{i}"))
                .Small()
                .Sends(new EngineerSelectFile(del)));
        }

        var filesColumn = VStack(Space.Xs, [.. fileItems]);

        // GitHub Desktop 2-column split: slim Changed Files list + the selected candidate's diff as a read-only code block.
        return Split(
            filesColumn,
            CodeBlock(state.SelectedFilePath, CodePreviewOf(state), state.SelectedLanguage, maxHeight: CodePreviewHeight),
            leadingExtent: 232,
            gap: Space.Sm);
    }

    private static Node RenderRightInspector(EngineerWorkspaceState state, string activeTab, Children? children)
    {
        var winner = state.Candidates.ElementAtOrDefault(state.SelectedInstanceIndex)
            ?? state.Candidates.FirstOrDefault();
        var shortSha = winner?.CommitSha is { Length: >= 8 } sha ? sha[..8] : "pending";
        var planNote = string.IsNullOrWhiteSpace(state.ConfirmationPlanId) ? string.Empty : $" · Plan: {state.ConfirmationPlanId}";

        var livePrNodes = RenderLivePrReviewNodes(state);

        return VStack(Space.Sm, [
            SectionHeader(
                state.PrPublished
                    ? $"Draft PR Published · {state.PullRequestUrl}"
                    : string.Create(CultureInfo.InvariantCulture, $"Host-Rendered Unified Diff Preview · Candidate w{state.SelectedInstanceIndex + 1} ({shortSha})"),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Winning candidate: w{state.SelectedInstanceIndex + 1} (#{state.SelectedInstanceIndex} · {winner?.StrategyLabel ?? "Surgical Patch"} · Score {winner?.TotalScore ?? 0}) · Branch: {winner?.BranchName ?? "ino/feature"} · Commit: {shortSha}{planNote}")),
            MutedText(state.StatusBanner),
            HStack(Space.Xs)[
                activeTab == "plan"
                    ? (state.LiveViewportPeek ? PrimaryButton("Hide Live Peek", "eye-off") : GhostButton("Live Peek", "eye"))
                        .Key("engineer:viewport-peek")
                        .Small()
                        .Sends(new EngineerTogglePeek())
                    : GhostButton("3D Flow", "git-branch")
                        .Key("engineer:back-to-flow")
                        .Small()
                        .Sends(new EngineerSelectTab("plan")),
                PrimaryButton("Approve & Open Draft PR", "git-pull-request")
                    .Key("engineer:pr:approve")
                    .Small()
                    .Guard(state.ReadyForPrApproval())
                    .Sends(new EngineerApprovePr()),
                DangerButton("Clean Worktrees", "trash")
                    .Key("engineer:pr:clean")
                    .Small()
                    .Guard(state.HasActiveWorktrees())
                    .Sends(new EngineerCleanWorktree())
            ],
            HStack(Space.Xs)[
                new Button("engineer:open-github-settings", "Configure GitHub ↗", Intent: AppSignals.OpenApp("settings", "connections/github"))
                {
                    Small = true,
                    Icon = "settings",
                },
                new Button(
                    "engineer:ask-ino",
                    "Ask Ino ↗",
                    Intent: AppSignals.OpenApp(
                        "ino",
                        new InoNavArgs(
                            Prompt: $"Review mission '{state.Goal}' on {state.RepositoryId}",
                            FocusTarget: $"focus:engineer/{state.ActiveFlowId ?? state.ActiveRunId}"),
                        fromNeuron: AppOwner))
                {
                    Small = true,
                    Icon = Icons.Sparkles,
                }
            ],
            .. livePrNodes,
            children?.Table(EngineerApp.TableKey, TableOf(state))
        ]);
    }

    private static Node[] RenderLivePrReviewNodes(EngineerWorkspaceState state)
    {
        var checks = state.EffectiveLivePrChecks;
        var threads = state.EffectiveLivePrReviewThreads;
        if (!state.LiveRepositoryConnected && checks.Length == 0 && threads.Length == 0)
        {
            return [];
        }

        var nodes = new List<Node>
        {
            SectionHeader(
                "Live GitHub PR Checks",
                state.LivePrReviewSummary ?? string.Create(CultureInfo.InvariantCulture, $"PR #{state.LivePullRequestNumber ?? 0}")),
        };

        if (checks.Length > 0)
        {
            var pills = checks
                .Take(8)
                .Select(static check =>
                {
                    var kind = string.Equals(check.Conclusion, "success", StringComparison.OrdinalIgnoreCase)
                        ? StatusKind.Done
                        : check.Conclusion is "failure" or "cancelled" or "timed_out" or "action_required"
                            ? StatusKind.Error
                            : StatusKind.Working;
                    var reqTag = check.IsRequired ? " [required]" : string.Empty;
                    var outcome = check.Conclusion ?? check.Status;
                    return StatusPill(kind, $"{check.Name}: {outcome}{reqTag}");
                })
                .ToArray();
            nodes.Add(HStack(Space.Xs, pills));
        }

        if (threads.Length > 0)
        {
            nodes.Add(SectionHeader(
                "Review Comment Threads",
                string.Create(CultureInfo.InvariantCulture, $"{threads.Length} comment(s)")));
            foreach (var thread in threads.Take(6))
            {
                var location = string.IsNullOrWhiteSpace(thread.FilePath) ? string.Empty : $" ({Path.GetFileName(thread.FilePath)})";
                var replyControl = thread.Replied
                    ? StatusPill(StatusKind.Done, $"Replied: {thread.ReplyBody}")
                    : (Node)GhostButton("Reply", "message-square")
                        .Key(string.Create(CultureInfo.InvariantCulture, $"engineer:pr:reply:{thread.CommentId}"))
                        .Small()
                        .Sends(new EngineerReplyReview(state.LivePullRequestNumber ?? 0, thread.CommentId, "Addressed in verified candidate worktree."));
                nodes.Add(HStack(Space.Xs)[
                    MutedText($"@{thread.Author}{location}: {thread.Body}"),
                    replyControl
                ]);
            }
        }

        return nodes.ToArray();
    }

    private static (int Additions, int Deletions) EstimateLineCounts(string? unifiedDiff, int changedLines, int fileCount)
    {
        if (!string.IsNullOrWhiteSpace(unifiedDiff))
        {
            var add = 0;
            var del = 0;
            foreach (var rawLine in unifiedDiff.Split('\n'))
            {
                if (rawLine.StartsWith("+", StringComparison.Ordinal) && !rawLine.StartsWith("+++", StringComparison.Ordinal))
                {
                    add++;
                }
                else if (rawLine.StartsWith("-", StringComparison.Ordinal) && !rawLine.StartsWith("---", StringComparison.Ordinal))
                {
                    del++;
                }
            }

            if (add > 0 || del > 0)
            {
                return (add, del);
            }
        }

        if (changedLines > 0)
        {
            return (Math.Max(1, changedLines * 3 / 4), Math.Max(0, changedLines / 4));
        }

        return (fileCount * 6, fileCount * 2);
    }

    // The code block of the Changes & Diff and PR tabs fills the pane beside the file list.
    private const double CodePreviewHeight = 1200;

    public static TabStripProps TabsOf(EngineerWorkspaceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(
        [
            new TabStripItem("plan", "3D Flow & Plan"),
            new TabStripItem("code", "Changes & Diff"),
            new TabStripItem("verify", "Verification & Traces"),
            new TabStripItem("pr", "PR Review"),
        ],
            AutomationName: "Engineer workspace tabs",
            SelectedId: EngineerWorkspaceState.NormalizeTab(state.ActiveTab));
    }

    // One folder per candidate worktree ("wt-{n}") holding its changed files ("file-{n}:{path}"). Engineer owns the selection (the
    // selected candidate's file) and the open folders (all but the ones the person closed), so it opens the tree to what it selected.
    public static TreeProps WorktreesOf(EngineerWorkspaceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var nodes = new List<TreeItem>();
        foreach (var c in state.Candidates)
        {
            var folderId = string.Create(CultureInfo.InvariantCulture, $"wt-{c.InstanceIndex}");
            var selectedTag = c.InstanceIndex == state.SelectedInstanceIndex ? " [SELECTED]" : string.Empty;
            var scoreTag = c.TotalScore > 0
                ? string.Create(CultureInfo.InvariantCulture, $" · {c.TotalScore} pts{selectedTag}")
                : selectedTag;
            nodes.Add(new TreeItem(
                folderId,
                string.Create(CultureInfo.InvariantCulture, $"w{c.InstanceIndex + 1} #{c.InstanceIndex} · {c.Backend} ({c.Status}){scoreTag}"),
                IsFolder: true));
            foreach (var file in c.ChangedFiles.Distinct(StringComparer.Ordinal))
            {
                nodes.Add(new TreeItem(
                    string.Create(CultureInfo.InvariantCulture, $"file-{c.InstanceIndex}:{file}"),
                    Path.GetFileName(file),
                    ParentId: folderId));
            }
        }

        var closed = state.ClosedWorktreeFolders ?? [];
        var expanded = nodes
            .Where(n => n.IsFolder && !closed.Contains(n.Id, StringComparer.Ordinal))
            .Select(static n => n.Id)
            .ToArray();
        return new TreeProps(
            nodes,
            AutomationName: "Worktrees",
            SelectedId: string.Create(CultureInfo.InvariantCulture, $"file-{state.SelectedInstanceIndex}:{state.SelectedFilePath}"),
            ExpandedIds: expanded);
    }

    public static ChatThreadProps BrainstormOf()
        => new(
            Placeholder: "Ask the User, Architect and Critic to plan a goal",
            EmptyText: "No brainstorm yet.",
            AutomationName: "Brainstorm");

    // The table follows the tab: the candidate tournament on PR Review, candidates and verification spans on Verification & Traces,
    // and the task checkpoints otherwise. Row ids are the candidate, span or task; the table owns its query and selection.
    public static QueryTableProps TableOf(EngineerWorkspaceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var tab = EngineerWorkspaceState.NormalizeTab(state.ActiveTab);
        if (tab == "pr")
        {
            return new QueryTableProps(
                "Parallel Candidate Tournament Scoreboard",
                [
                    new QueryTableColumn("candidate", "Candidate"),
                    new QueryTableColumn("strategy", "Strategy"),
                    new QueryTableColumn("branch", "Branch & Commit"),
                    new QueryTableColumn("gates", "Gates & Diff"),
                    new QueryTableColumn("score", "Score / Promotion"),
                ],
                Rows: Unique(state.Candidates.Select(c =>
                {
                    var shortSha = ShortSha(c.CommitSha);
                    var isSelected = c.InstanceIndex == state.SelectedInstanceIndex;
                    var failed = IsFailed(c);
                    var promotion = failed
                        ? "FAILED"
                        : string.Create(CultureInfo.InvariantCulture, $"{c.TotalScore} · {(isSelected ? "SELECTED" : "STANDBY")} (Rank #{c.Rank})");
                    var gateDetail = c.TotalScore > 0 || failed
                        ? string.Create(
                            CultureInfo.InvariantCulture,
                            $"Build={(c.BuildPassed ? "PASS" : "FAIL")} · Tests={(c.TestsPassed ? "PASS" : "FAIL")} · Live={(c.LiveVerifyPassed ? "PASS" : "FAIL")} · Δ{c.ChangedLines} lines")
                        : string.Create(CultureInfo.InvariantCulture, $"{c.Status} · {c.ChangedFiles.Length} file(s)");
                    return new QueryTableRow(
                        string.Create(CultureInfo.InvariantCulture, $"w{c.InstanceIndex + 1}"),
                        [
                            string.Create(CultureInfo.InvariantCulture, $"w{c.InstanceIndex + 1} (#{c.InstanceIndex})"),
                            c.StrategyLabel,
                            $"{c.BranchName} @ {shortSha}",
                            gateDetail,
                            promotion,
                        ]);
                })),
                AutomationName: "Candidate scoreboard");
        }

        if (tab == "verify")
        {
            var candidateRows = state.Candidates.Select(c =>
            {
                var isSelected = c.InstanceIndex == state.SelectedInstanceIndex;
                var rowStatus = IsFailed(c)
                    ? "FAIL"
                    : state.VerificationPassed
                        ? (isSelected ? "PASS · SELECTED" : "PASS")
                        : (isSelected ? "READY · SELECTED" : "READY");
                var step = string.Create(CultureInfo.InvariantCulture, $"candidate-w{c.InstanceIndex + 1}");
                return new QueryTableRow(
                    step,
                    [
                        step,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"w{c.InstanceIndex + 1} ({c.StrategyLabel}) · Score={c.TotalScore} · Δ={c.ChangedLines} lines · {c.BranchName}@{ShortSha(c.CommitSha)}"),
                        rowStatus,
                    ]);
            });
            var traceRows = state.DiagnosticsAndTraces.Select((line, idx) =>
            {
                var step = string.Create(CultureInfo.InvariantCulture, $"gate-{idx + 1}");
                return new QueryTableRow(step, [step, line, state.VerificationPassed ? "PASS" : "READY"]);
            });
            return new QueryTableProps(
                "Candidate Scoreboard & Verification Spans",
                [
                    new QueryTableColumn("step", "Candidate / Span"),
                    new QueryTableColumn("detail", "Observed Outcome"),
                    new QueryTableColumn("status", "Status"),
                ],
                Rows: Unique(candidateRows.Concat(traceRows)),
                AutomationName: "Verification spans");
        }

        var checkpoint = state.VerificationPassed ? "verified" : state.PlanApproved ? "checkpointed" : "planned";
        return new QueryTableProps(
            "Task Checkpoints & Roslyn Diagnostics",
            [
                new QueryTableColumn("id", "Task"),
                new QueryTableColumn("title", "Title"),
                new QueryTableColumn("lang", "Language"),
                new QueryTableColumn("status", "Checkpoint"),
            ],
            Rows: Unique(state.Tasks.Select(t => new QueryTableRow(t.Id, [t.Id, t.Title, t.Language, checkpoint]))),
            AutomationName: "Task checkpoints");
    }

    // The selected candidate's diff, or a sample of the selected file when the candidate has none yet.
    public static string CodePreviewOf(EngineerWorkspaceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var active = state.Candidates.ElementAtOrDefault(state.SelectedInstanceIndex)
            ?? state.Candidates.FirstOrDefault();
        var candidateLabel = string.Create(CultureInfo.InvariantCulture, $"w{state.SelectedInstanceIndex + 1}");
        var commitSha = active?.CommitSha ?? "pending";
        var strategy = active?.StrategyLabel ?? "Minimal Surgical Patch";
        var branch = active?.BranchName ?? "ino/feature";
        var score = active?.TotalScore ?? 0;
        var changedLines = active?.ChangedLines ?? 0;

        if (!string.IsNullOrWhiteSpace(active?.UnifiedDiff))
        {
            return $$"""
                // Candidate {{candidateLabel}} (#{{state.SelectedInstanceIndex}} · {{strategy}})
                // Branch: {{branch}} · Commit: {{commitSha}} · Score: {{score}} (Δ{{changedLines}} lines) · File: {{state.SelectedFilePath}}
                {{active.UnifiedDiff}}
                """;
        }

        if (state.SelectedLanguage == "dart")
        {
            return $$"""
                // Candidate {{candidateLabel}} ({{strategy}}) · {{branch}} · {{state.SelectedFilePath}}
                class StepperStageBadge extends StatelessWidget {
                  const StepperStageBadge({super.key, required this.stage, required this.status});

                  final String stage;
                  final String status;

                  @override
                  Widget build(BuildContext context) {
                    return Semantics(
                      identifier: 'engineer:stage-badge',
                      container: true,
                      child: Text('$stage · $status'),
                    );
                  }
                }
                """;
        }

        return $$"""
            // Candidate {{candidateLabel}} (#{{state.SelectedInstanceIndex}} · {{strategy}}) · {{branch}} · {{state.SelectedFilePath}}
            namespace IntoChat.OS.Modules.Apps.Flows;

            [DigitalBrain.Neurons.Neuron(IEngineerRuns.EngineerFlow)]
            internal sealed class EngineerFlow : Flow<EngineerRun>, IFlowStarter
            {
                // Goal: {{state.Goal}}
                // Parallel worktrees: {{state.InstanceCount}} · Verified: {{state.VerificationPassed}} · Commit: {{commitSha}}
            }
            """;
    }

    private static bool IsFailed(EngineerCandidateSummary candidate) => candidate.Status is "failed" or "refused" or "error";

    private static string ShortSha(string? sha) => sha is { Length: >= 8 } ? sha[..8] : "pending";

    // A row id is unique within a table: a repeated id keeps its first row.
    private static QueryTableRow[] Unique(IEnumerable<QueryTableRow> rows)
        => [.. rows.DistinctBy(static row => row.Id, StringComparer.Ordinal)];
}

