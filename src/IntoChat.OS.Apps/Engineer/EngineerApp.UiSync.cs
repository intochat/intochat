/*
# IntoChat Engineer
Role: Studio
Shipped: true
Operations: open, new-run, brainstorm, approve-plan, run-verify, approve-pr, clean-worktree, select-tab, select-instance, select-candidate, reply-review, ui.command

Autonomous multi-worktree engineering workbench (`app.engineer`) orchestrating `flow.engineer` across 1..N isolated
local git worktrees or cloud sandbox volumes with User + Architect + Critic brainstorming, Roslyn C# + Dart code edits,
Release build/test + live Playwright/MCP verification, parallel candidate tournament scoring, and SHA-bound human PR approval.
*/
using System.Globalization;
using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.Microsoft.GitHub.Signals;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.UI.Chat;
using IntoChat.OS.Modules.UI.Chat.Signals;
using IntoChat.OS.Modules.UI.CodeEditor;
using IntoChat.OS.Modules.UI.CommandBar;
using IntoChat.OS.Modules.UI.CommandBar.Signals;
using IntoChat.OS.Modules.UI.KanbanBoard;
using IntoChat.OS.Modules.UI.Stepper;
using IntoChat.OS.Modules.UI.Stepper.Signals;
using IntoChat.OS.Modules.UI.Table;
using IntoChat.OS.Modules.UI.Tabs;
using IntoChat.OS.Modules.UI.Tabs.Signals;
using IntoChat.OS.Modules.UI.TreeView;
using IntoChat.OS.Modules.UI.TreeView.Signals;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Engineer;

public sealed partial class EngineerApp
{
    private async Task SyncUiNeuronsAsync(EngineerWorkspaceState state)
    {
        try
        {
            if (CommandBar is { } cmdBar)
            {
                await cmdBar.Configure(
                [
                    new CommandBarItem("new-run", "New Run", "plus"),
                    new CommandBarItem("brainstorm", "Brainstorm", "sparkles"),
                    new CommandBarItem("approve-plan", "Approve Plan", "check", Disabled: state.PlanReadyToApprove().Broken),
                    new CommandBarItem("run-verify", "Run Verify", "play", Disabled: state.ReadyForVerification().Broken),
                    new CommandBarItem("approve-pr", "Approve PR", "git-pull-request", Disabled: state.ReadyForPrApproval().Broken),
                    new CommandBarItem("clean-worktree", "Clean Worktree", "trash", Primary: false, Disabled: state.HasActiveWorktrees().Broken),
                ]);
            }

            if (Stepper is { } stepper)
            {
                var stepState = await stepper.Configure(BuildStepperSteps(state), state.CurrentStage, "IntoChat-Engineer Pipeline");
                _syncedStepperVersion = Math.Max(_syncedStepperVersion, stepState.Version);
            }

            if (WorkspaceTabs is { } tabs)
            {
                var tabState = await tabs.Configure(
                [
                    new UiTabItem("plan", "3D Flow & Plan"),
                    new UiTabItem("code", "Changes & Diff"),
                    new UiTabItem("verify", "Verification & Traces"),
                    new UiTabItem("pr", "PR Review"),
                ], state.ActiveTab);
                _syncedTabsVersion = Math.Max(_syncedTabsVersion, tabState.Version);
            }

            if (WorktreeTree is { } tree)
            {
                var nodes = new List<TreeNode>();
                var expanded = new List<string>();
                foreach (var c in state.Candidates)
                {
                    var folderId = string.Create(CultureInfo.InvariantCulture, $"wt-{c.InstanceIndex}");
                    expanded.Add(folderId);
                    var scoreTag = c.TotalScore > 0
                        ? string.Create(
                            CultureInfo.InvariantCulture,
                            $" · {c.TotalScore} pts{(c.InstanceIndex == state.SelectedInstanceIndex ? " [SELECTED]" : string.Empty)}")
                        : c.InstanceIndex == state.SelectedInstanceIndex ? " [SELECTED]" : string.Empty;
                    nodes.Add(new TreeNode(
                        folderId,
                        string.Create(CultureInfo.InvariantCulture, $"w{c.InstanceIndex + 1} #{c.InstanceIndex} · {c.Backend} ({c.Status}){scoreTag}"),
                        IsFolder: true));
                    foreach (var file in c.ChangedFiles)
                    {
                        var fileId = string.Create(CultureInfo.InvariantCulture, $"file-{c.InstanceIndex}:{file}");
                        nodes.Add(new TreeNode(fileId, Path.GetFileName(file), ParentId: folderId));
                    }
                }

                var selectedTreeId = string.Create(CultureInfo.InvariantCulture, $"file-{state.SelectedInstanceIndex}:{state.SelectedFilePath}");
                var treeState = await tree.Configure(nodes, expandedIds: expanded, selectedId: selectedTreeId);
                _syncedTreeVersion = Math.Max(_syncedTreeVersion, treeState.Version);
            }

            if (CodeEditor is { } editor)
            {
                await editor.Configure(
                    state.SelectedFilePath,
                    state.SelectedLanguage,
                    BuildCodePreview(state),
                    readOnly: false);
            }

            if (Kanban is { } kanban)
            {
                KanbanColumn[] cols =
                [
                    new("planned", "Planned", "list"),
                    new("active", "In Worktree", "code"),
                    new("verified", "Verified", "check"),
                ];
                var targetCol = state.VerificationPassed ? "verified" : state.PlanApproved ? "active" : "planned";
                var cards = state.Tasks.Select((t, idx) => new KanbanCard(
                    t.Id,
                    targetCol,
                    t.Title,
                    Subtitle: string.Join(", ", t.TargetFiles),
                    Tag: t.Language,
                    Order: idx)).ToArray();
                await kanban.Configure("Engineer Task Graph", cols, cards, state.Tasks.FirstOrDefault()?.Id);
            }

            if (TasksTable is { } table)
            {
                if (state.ActiveTab == "pr")
                {
                    var scoreboardRows = state.Candidates
                        .Select(c =>
                        {
                            var shortSha = c.CommitSha is { Length: >= 8 } sha ? sha[..8] : "pending";
                            var isSelected = c.InstanceIndex == state.SelectedInstanceIndex;
                            var failed = c.Status is "failed" or "refused" or "error";
                            var promotion = failed
                                ? "FAILED"
                                : isSelected
                                    ? string.Create(CultureInfo.InvariantCulture, $"{c.TotalScore} · SELECTED (Rank #{c.Rank})")
                                    : string.Create(CultureInfo.InvariantCulture, $"{c.TotalScore} · STANDBY (Rank #{c.Rank})");
                            var gateDetail = c.TotalScore > 0 || failed
                                ? string.Create(
                                    CultureInfo.InvariantCulture,
                                    $"Build={(c.BuildPassed ? "PASS" : "FAIL")} · Tests={(c.TestsPassed ? "PASS" : "FAIL")} · Live={(c.LiveVerifyPassed ? "PASS" : "FAIL")} · Δ{c.ChangedLines} lines")
                                : string.Create(CultureInfo.InvariantCulture, $"{c.Status} · {c.ChangedFiles.Length} file(s)");
                            return (IReadOnlyList<string>)
                            [
                                string.Create(CultureInfo.InvariantCulture, $"w{c.InstanceIndex + 1} (#{c.InstanceIndex})"),
                                c.StrategyLabel,
                                $"{c.BranchName} @ {shortSha}",
                                gateDetail,
                                promotion,
                            ];
                        })
                        .ToArray();

                    await table.Render(
                        "Parallel Candidate Tournament Scoreboard",
                        [
                            new TableColumn("candidate", "Candidate"),
                            new TableColumn("strategy", "Strategy"),
                            new TableColumn("branch", "Branch & Commit"),
                            new TableColumn("gates", "Gates & Diff"),
                            new TableColumn("score", "Score / Promotion"),
                        ],
                        scoreboardRows);
                }
                else if (state.ActiveTab == "verify")
                {
                    var candidateRows = state.Candidates.Select(c =>
                    {
                        var shortSha = c.CommitSha is { Length: >= 8 } sha ? sha[..8] : "pending";
                        var isSelected = c.InstanceIndex == state.SelectedInstanceIndex;
                        var failed = c.Status is "failed" or "refused" or "error";
                        var rowStatus = failed
                            ? "FAIL"
                            : state.VerificationPassed
                                ? (isSelected ? "PASS · SELECTED" : "PASS")
                                : (isSelected ? "READY · SELECTED" : "READY");
                        return (IReadOnlyList<string>)
                        [
                            string.Create(CultureInfo.InvariantCulture, $"candidate-w{c.InstanceIndex + 1}"),
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"w{c.InstanceIndex + 1} ({c.StrategyLabel}) · Score={c.TotalScore} · Δ={c.ChangedLines} lines · {c.BranchName}@{shortSha}"),
                            rowStatus,
                        ];
                    });

                    var traceRows = state.DiagnosticsAndTraces.Select((line, idx) => (IReadOnlyList<string>)
                    [
                        string.Create(CultureInfo.InvariantCulture, $"gate-{idx + 1}"),
                        line,
                        state.VerificationPassed ? "PASS" : "READY",
                    ]);

                    await table.Render(
                        "Candidate Scoreboard & Verification Spans",
                        [
                            new TableColumn("step", "Candidate / Span"),
                            new TableColumn("detail", "Observed Outcome"),
                            new TableColumn("status", "Status"),
                        ],
                        [.. candidateRows, .. traceRows]);
                }
                else
                {
                    await table.Render(
                        "Task Checkpoints & Roslyn Diagnostics",
                        [
                            new TableColumn("id", "Task"),
                            new TableColumn("title", "Title"),
                            new TableColumn("lang", "Language"),
                            new TableColumn("status", "Checkpoint"),
                        ],
                        state.Tasks.Select(t => (IReadOnlyList<string>)
                        [
                            t.Id,
                            t.Title,
                            t.Language,
                            state.VerificationPassed ? "verified" : state.PlanApproved ? "checkpointed" : "planned",
                        ]).ToArray());
                }
            }

            if (BrainstormChat is { } chat)
            {
                var chatSig = string.Create(CultureInfo.InvariantCulture, $"{state.ActiveRunId}:{state.BrainstormTurns.Length}:{state.Goal}");
                if (_lastSyncedChatSignature != chatSig)
                {
                    if (_lastSyncedChatSignature is null && (await chat.Read()).Version == 0)
                    {
                        await chat.Configure(
                            "engineer-brainstorm",
                            "Architect · Critic · User Planning",
                            "claude-sonnet-4-5",
                            [
                                new ChatModelOption("claude-sonnet-4-5", "Claude Sonnet 4.5", "Anthropic"),
                                new ChatModelOption("gpt-5-thinking", "GPT-5 Thinking", "OpenAI"),
                                new ChatModelOption("qwen2.5-coder:14b", "Qwen 2.5 Coder (Local)", "Ollama"),
                            ]);
                    }

                    for (var i = 0; i < state.BrainstormTurns.Length; i++)
                    {
                        var turn = state.BrainstormTurns[i];
                        var role = turn.Role == "user" ? "user" : "assistant";
                        var msgId = string.Create(CultureInfo.InvariantCulture, $"{state.ActiveRunId}-turn-{i}");
                        await chat.AppendMessage(new ChatMessage(
                            Id: msgId,
                            Role: role,
                            Text: $"**[{turn.Speaker} · {turn.Perspective}]** {turn.Message}",
                            Status: "done"));
                    }

                    _lastSyncedChatSignature = chatSig;
                }
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "EngineerApp UI neuron sync skipped in minimal test host.");
        }
    }

    private static StepperStep[] BuildStepperSteps(EngineerWorkspaceState state)
    {
        var activeIdx = Array.IndexOf(EngineerWorkspaceState.PipelineStageIds, state.CurrentStage);
        if (activeIdx < 0) { activeIdx = 0; }

        StepperStep Make(int idx, string id, string title, string icon)
        {
            var status = state.PrPublished || idx < activeIdx
                ? StepperStatuses.Done
                : idx == activeIdx
                    ? StepperStatuses.Active
                    : StepperStatuses.Pending;
            return new StepperStep(id, title, status, Icon: icon);
        }

        return
        [
            Make(0, "brainstorm-and-plan", "Brainstorm & Plan", "sparkles"),
            Make(1, "prepare-worktree", "Prepare Worktree", "git-branch"),
            Make(2, "implement-tasks", "Implement Tasks", "code"),
            Make(3, "build-and-test", "Build & Test", "hammer"),
            Make(4, "live-verify", "Live Verify", "globe"),
            Make(5, "review-and-pr", "Review & PR", "git-pull-request"),
        ];
    }

    private static string BuildCodePreview(EngineerWorkspaceState state)
    {
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
                      identifier: 'neuron:ui.stepper:engineer/stepper',
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

            [DigitalBrain.Neurons.Neuron(IFlows.EngineerFlow)]
            internal sealed class EngineerFlow : Flow<EngineerRun>, IFlowStarter
            {
                // Goal: {{state.Goal}}
                // Parallel worktrees: {{state.InstanceCount}} · Verified: {{state.VerificationPassed}} · Commit: {{commitSha}}
            }
            """;
    }
}
