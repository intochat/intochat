using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.UI;
using IntoChat.OS.Apps.AgentApp;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.Social;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Tasks;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Modules.Microsoft.Playwright;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using TaskStatus = IntoChat.OS.Modules.Apps.Tasks.TaskStatus;

namespace IntoChat.OS.Tests.Browser;

/// <summary>
/// Wave 4 INT-2606 Part 2: Master Plan Nightly Playwright Browser Gauntlet.
/// Proves all Wave 2–3 user journeys end-to-end in real headless Chromium against live IntoChat.OS + Flutter Web Shell:
/// <list type="number">
/// <item>Unified 264px Arc Messenger Shell, live folder assignment, Settings People &amp; Team, and retirement parity (INT-2605 B6).</item>
/// <item>Ino Engineer PR Drawer (Tournament | Diff | Logs), LocalSelfReview gate, and SHA-locked Draft PR approval (INT-2603 A5-UI).</item>
/// <item>Custom Specialist Bot (@handle with IsolatedBotScope) + Orca-style Multi-Agent Squad Room, Kanban Plan Drawer, and ChoiceCard Return-to-Choice (INT-2602 B4 + INT-2604 B5).</item>
/// <item>Cross-Brain Shared Room + live [Shareable] RoomChecklistApp guest mini-app with fail-closed privacy (INT-2601 B3 + INT-2605 B6).</item>
/// </list>
/// </summary>
[Collection(nameof(Item350WebShellCollection))]
public sealed class MasterPlanBrowserGauntletFacts(Item350WebShellFixture host)
{
    private const int ViewportWidth = 1440;
    private const int ViewportHeight = 900;

    [Fact]
    public async Task Journey1_ArcMessengerShell_FolderAssignment_And_RetirementParity()
    {
        var ct = TestContext.Current.CancellationToken;
        var brainId = new BrainId(host.Scope);
        var pinnedThreadId = $"gauntlet-pin-{Guid.NewGuid():N}"[..20];
        var folderThreadId = $"gauntlet-fld-{Guid.NewGuid():N}"[..20];
        var todayThreadId = $"gauntlet-day-{Guid.NewGuid():N}"[..20];
        const string folderName = "Master Plan Ops";

        using (host.StampOwner())
        {
            var directory = SiloNeurons.Get<IThreadDirectory>(host.Services, new NeuronId(brainId, Threads.Type, IThreadDirectory.DefaultKey));
            await directory.Create(pinnedThreadId, "Pinned Architecture", ThreadTrustPosture.OwnerTrusted, ThreadKind.DirectAssistant);
            await directory.SetPinned(pinnedThreadId, true);
            await directory.Create(folderThreadId, "Release Checklist", ThreadTrustPosture.OwnerTrusted, ThreadKind.DirectAssistant);
            await directory.Create(todayThreadId, "Morning Standup", ThreadTrustPosture.OwnerTrusted, ThreadKind.DirectAssistant);
        }

        await using var context = await host.NewBrowserContextAsync(ViewportWidth, ViewportHeight, ct);
        var page = await context.NewPageAsync();
        var errors = TrackBrowserErrors(page);

        try
        {
            // 1. Load IntoChat OS web shell directly into InoApp (app.ino) and verify the 264px Arc sidebar sections.
            await page.GotoAsync(host.ShellFor("apps/ino/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
            await page.Locator("flt-semantics-host").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);

            await WaitForSemanticsTextContainsAsync(page, "Favourites", ct);
            await WaitForSemanticsTextContainsAsync(page, "Pinned", ct);
            await WaitForSemanticsTextContainsAsync(page, "Pinned Architecture", ct);
            await WaitForSemanticsTextContainsAsync(page, "Today", ct);
            await WaitForSemanticsTextContainsAsync(page, "Morning Standup", ct);
            await WaitForSemanticsTextContainsAsync(page, "History", ct);

            // 2. Open the "+ New" split menu in Chromium and verify New Specialist / New Squad triggers render.
            await page.ClickButtonAsync(InoArcSidebar.NewSplitMenuKey, ct);
            await page.KeyedNode("ino:new-specialist").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await page.KeyedNode("ino:menu-new-squad").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await WaitForSemanticsTextContainsAsync(page, "New specialist", ct);
            await WaitForSemanticsTextContainsAsync(page, "New squad", ct);

            // Close the split menu cleanly.
            await page.ClickButtonAsync(InoArcSidebar.NewSplitMenuKey, ct);

            // 3. Assign a folder via ino.set-folder and verify the rendered Arc sidebar updates live under Squads & Projects.
            using (var kernel = host.CreateKernelClient())
            {
                using var setFolder = await kernel.PostAsJsonAsync(
                    $"/brains/{host.Scope}/act",
                    new
                    {
                        neuron = $"{host.Scope}/apps/ino",
                        target = ".",
                        signal = "ino.set-folder",
                        args = new { conversationId = folderThreadId, folder = folderName },
                    },
                    ct);
                await AssertActSucceededAsync(setFolder, "ino.set-folder", ct);
            }

            await WaitForSemanticsTextContainsAsync(page, "Squads & Projects", ct);
            await WaitForSemanticsTextContainsAsync(page, folderName, ct);
            await WaitForSemanticsTextContainsAsync(page, "Release Checklist", ct);

            // 4. Verify retirement parity: app.friends is absent from desktop dock & hosted types, and agentapp routes to app.ino.
            var hostedTypes = host.Services.GetRequiredService<IHostedNeuronTypes>();
            Assert.False(hostedTypes.IsHosted("app.friends"), "Retired app.friends must not be hosted.");
            Assert.False(hostedTypes.IsHosted("app.agent"), "Retired agentapp screen must not be hosted.");

            using (var kernel = host.CreateKernelClient())
            {
                var desktopViewJson = await kernel.GetStringAsync($"/brains/{host.Scope}/apps/desktop/view", ct);
                Assert.Contains("\"dock-ino\"", desktopViewJson, StringComparison.Ordinal);
                Assert.Contains("\"dock-settings\"", desktopViewJson, StringComparison.Ordinal);
                Assert.DoesNotContain("\"dock-friends\"", desktopViewJson, StringComparison.Ordinal);

                using var retiredFriendsResp = await kernel.GetAsync($"/brains/{host.Scope}/apps/friends/view", ct);
                Assert.Equal(HttpStatusCode.NotFound, retiredFriendsResp.StatusCode);
            }

            // 5. Verify Settings People & Team section renders cleanly in Chromium (replacement for retired FriendsApp roster/codes).
            using (var kernel = host.CreateKernelClient())
            {
                using var navTeam = await kernel.PostAsJsonAsync(
                    $"/brains/{host.Scope}/act",
                    new
                    {
                        neuron = $"{host.Scope}/apps/settings",
                        target = ".",
                        signal = "settings.navigate",
                        args = new { section = "team" },
                    },
                    ct);
                await AssertActSucceededAsync(navTeam, "settings.navigate(team)", ct);
            }

            await page.GotoAsync(host.ShellFor("apps/settings/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
            await WaitForSemanticsTextContainsAsync(page, "People & Team", ct);
            await WaitForSemanticsTextContainsAsync(page, "Mint 24h friend code", ct);
        }
        catch (Exception ex)
        {
            throw await BuildDiagnosticFailureAsync("Journey 1 (Arc Messenger Shell & Retirement Parity)", page, errors, ex, ct);
        }

        Assert.Empty(errors);
    }

    [Fact]
    public async Task Journey2_EngineerPrDrawer_TournamentDiffLogs_And_ShaLockedApproval()
    {
        var ct = TestContext.Current.CancellationToken;
        var brainId = new BrainId(host.Scope);
        var failingFlowId = $"{host.Scope}/{IEngineerRuns.EngineerFlow}/gauntlet-fail-{Guid.NewGuid():N}"[..48];
        var passingFlowId = $"{host.Scope}/{IEngineerRuns.EngineerFlow}/gauntlet-pass-{Guid.NewGuid():N}"[..48];
        const string failingSha = "deadbeef1234567890abcdef1234567890abcdef";
        const string passingSha = "abcdef1234567890abcdef1234567890abcdef12";
        var planId = $"plan-gauntlet-{Guid.NewGuid():N}"[..24];
        const string openedPrUrl = "https://github.com/intochat/intochat/pull/2606";

        host.EngineerRuns[failingFlowId] = CreateEngineerRunSummary(
            failingFlowId,
            failingSha,
            selfReviewPassed: false,
            selfReviewSummary: "failed (1 blocking: missing SHA guard)",
            confirmationPlanId: null,
            status: FlowStatus.Waiting);

        host.EngineerRuns[passingFlowId] = CreateEngineerRunSummary(
            passingFlowId,
            passingSha,
            selfReviewPassed: true,
            selfReviewSummary: "passed (0 blocking, 0 warning)",
            confirmationPlanId: planId,
            status: FlowStatus.Waiting);

        await using var context = await host.NewBrowserContextAsync(ViewportWidth, ViewportHeight, ct);
        var page = await context.NewPageAsync();
        var errors = TrackBrowserErrors(page);

        try
        {
            // Seed the real RequiresPerson ConfirmationPlan on the live silo for the passing run.
            using (host.StampOwner())
            {
                var confirmations = SiloNeurons.Get<IConfirmationRequests>(host.Services, IConfirmationRequests.Of(brainId));
                await confirmations.Request(new ConfirmationPlan(
                    Id: planId,
                    Action: ConfirmationAction.Other,
                    Destructive: true,
                    Title: "Push & open Draft PR?",
                    Body: "Push verified branch and open SHA-locked Draft PR",
                    ApproveLabel: "Push & Open Draft PR",
                    DenyLabel: "Cancel",
                    TargetLabel: "intochat/intochat")
                {
                    RequiresPerson = true,
                    Subject = passingSha,
                });
            }

            await page.GotoAsync(host.ShellFor("apps/ino/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
            await page.Locator("flt-semantics-host").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);

            // 1. Open the failing LocalSelfReview Engineer PR run: approval must be disabled, error banner shown, and no "Pin as App".
            using (var kernel = host.CreateKernelClient())
            {
                using var openFailing = await kernel.PostAsJsonAsync(
                    $"/brains/{host.Scope}/act",
                    new
                    {
                        neuron = $"{host.Scope}/apps/ino",
                        target = ".",
                        signal = "ino.open-drawer",
                        args = new
                        {
                            artifactId = failingFlowId,
                            flowId = failingFlowId,
                            title = "Gauntlet Failing PR Run",
                            kind = InoDrawerArtifact.EngineerPrKind,
                        },
                    },
                    ct);
                await AssertActSucceededAsync(openFailing, "ino.open-drawer (failing engineer-pr)", ct);
            }

            await WaitForSemanticsTextContainsAsync(page, "Self-Review Gate Failed", ct);
            await WaitForSemanticsTextContainsAsync(page, "SHA · deadbeef", ct);

            var disabledApproveBtn = page.GetByRole(AriaRole.Button, new() { Name = "Approve & Open Draft PR" }).First;
            await disabledApproveBtn.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            var isDisabled = await disabledApproveBtn.GetAttributeAsync("aria-disabled").WaitAsync(ct);
            Assert.Equal("true", isDisabled);

            var failingDrawerText = await ReadAllSemanticsTextAsync(page, ct);
            Assert.DoesNotContain("Pin as App", failingDrawerText, StringComparison.Ordinal);

            // 2. Open the passing verified FlowStatus.Waiting Engineer PR run and switch across Tournament | Diff | Logs live.
            using (var kernel = host.CreateKernelClient())
            {
                using var openPassing = await kernel.PostAsJsonAsync(
                    $"/brains/{host.Scope}/act",
                    new
                    {
                        neuron = $"{host.Scope}/apps/ino",
                        target = ".",
                        signal = "ino.open-drawer",
                        args = new
                        {
                            artifactId = passingFlowId,
                            flowId = passingFlowId,
                            title = "Gauntlet Verified PR Run",
                            kind = InoDrawerArtifact.EngineerPrKind,
                        },
                    },
                    ct);
                await AssertActSucceededAsync(openPassing, "ino.open-drawer (passing engineer-pr)", ct);
            }

            // Tournament tab (default)
            await WaitForSemanticsTextContainsAsync(page, "Self-Review Passed", ct);
            await WaitForSemanticsTextContainsAsync(page, "SHA · abcdef12", ct);
            await WaitForSemanticsTextContainsAsync(page, "Tournament Scoreboard", ct);
            await WaitForSemanticsTextContainsAsync(page, "Minimal Surgical Patch", ct);

            var enabledApproveBtn = page.GetByRole(AriaRole.Button, new() { Name = "Approve & Open Draft PR" }).First;
            await enabledApproveBtn.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            Assert.NotEqual("true", await enabledApproveBtn.GetAttributeAsync("aria-disabled").WaitAsync(ct));

            var passingDrawerText = await ReadAllSemanticsTextAsync(page, ct);
            Assert.DoesNotContain("Pin as App", passingDrawerText, StringComparison.Ordinal);

            // Switch to Diff tab
            await page.ClickButtonAsync(InoView.DrawerTabDiffKey, ct);
            await WaitForSemanticsTextContainsAsync(page, "Winning Unified Diff", ct);
            await WaitForSemanticsTextContainsAsync(page, "+++ b/apps/ino/InoView.cs", ct);

            // Switch to Logs tab
            await page.ClickButtonAsync(InoView.DrawerTabLogsKey, ct);
            await WaitForSemanticsTextContainsAsync(page, "Verification & Audit Logs", ct);
            await WaitForSemanticsTextContainsAsync(page, "mcp/health-probe", ct);
            await WaitForSemanticsTextContainsAsync(page, "playwright/drawer-smoke", ct);
            await WaitForSemanticsTextContainsAsync(page, "span/http.get.health 200 OK", ct);

            // Switch back to Tournament tab
            await page.ClickButtonAsync(InoView.DrawerTabPreviewKey, ct);
            await WaitForSemanticsTextContainsAsync(page, "Tournament Scoreboard", ct);

            // 3. Clicking "Approve & Open Draft PR" before resolving the confirmation card fails closed;
            //    resolving the real RequiresPerson ConfirmationPlan as owner transitions live to "Open PR on GitHub" (https://).
            await page.ClickButtonAsync(InoView.DrawerApprovePrKey, ct);
            await WaitForSemanticsTextContainsAsync(page, "Draft PR Approval Blocked", ct);

            host.EngineerRuns[passingFlowId] = CreateEngineerRunSummary(
                passingFlowId,
                passingSha,
                selfReviewPassed: true,
                selfReviewSummary: "passed (0 blocking, 0 warning)",
                confirmationPlanId: planId,
                status: FlowStatus.Completed,
                pushedRef: "refs/heads/ino/engineer-gauntlet",
                pullRequestUrl: openedPrUrl);

            using (host.StampOwner())
            {
                var inbox = SiloNeurons.Get<IConfirmationInbox>(host.Services, IConfirmationRequests.Of(brainId));
                await inbox.Resolve(planId, approve: true);
            }

            await WaitForSemanticsTextContainsAsync(page, "Open PR on GitHub", ct);
            await WaitForSemanticsTextContainsAsync(page, "Draft PR Opened", ct);
            await WaitForSemanticsTextContainsAsync(page, openedPrUrl, ct);

            await page.KeyedNode(InoView.DrawerOpenPrKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
        }
        catch (Exception ex)
        {
            throw await BuildDiagnosticFailureAsync("Journey 2 (Engineer PR Drawer & SHA-Locked Approval)", page, errors, ex, ct);
        }
        finally
        {
            host.EngineerRuns.TryRemove(failingFlowId, out _);
            host.EngineerRuns.TryRemove(passingFlowId, out _);
            await CloseDrawerBestEffortAsync(ct);
        }

        Assert.Empty(errors);
    }

    [Fact]
    public async Task Journey3_CustomSpecialist_SquadRoom_KanbanPlanDrawer_And_ChoiceCardReturn()
    {
        var ct = TestContext.Current.CancellationToken;
        var brainId = new BrainId(host.Scope);
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var specialistHandle = $"@gauntlet-{suffix}";
        var squadTitle = $"Gauntlet Squad {suffix}";
        string? createdTaskId = null;

        await using var context = await host.NewBrowserContextAsync(ViewportWidth, ViewportHeight, ct);
        var page = await context.NewPageAsync();
        var errors = TrackBrowserErrors(page);

        try
        {
            // 1. Create a custom specialist (@handle with IsolatedBotScope) via InoApp signal.
            using (var kernel = host.CreateKernelClient())
            {
                using var createSpecialist = await kernel.PostAsJsonAsync(
                    $"/brains/{host.Scope}/act",
                    new
                    {
                        neuron = $"{host.Scope}/apps/ino",
                        target = ".",
                        signal = "ino.new-specialist",
                        args = new
                        {
                            title = "Gauntlet Architect",
                            handle = specialistHandle,
                            instructions = "Review architecture and verify invariants.",
                            description = "Isolated architecture specialist.",
                            allowedContracts = new[] { typeof(ITasks).FullName! },
                            memoryScope = nameof(MemoryScope.IsolatedBotScope),
                        },
                    },
                    ct);
                await AssertActSucceededAsync(createSpecialist, "ino.new-specialist", ct);

                // 2. Create an OwnerTrusted Squad Room (@ino Lead + custom specialist).
                using var createSquad = await kernel.PostAsJsonAsync(
                    $"/brains/{host.Scope}/act",
                    new
                    {
                        neuron = $"{host.Scope}/apps/ino",
                        target = ".",
                        signal = "ino.new-squad",
                        args = new
                        {
                            title = squadTitle,
                            agentIds = new[] { "@ino", specialistHandle },
                            leadAgentId = "@ino",
                        },
                    },
                    ct);
                await AssertActSucceededAsync(createSquad, "ino.new-squad", ct);
            }

            // 3. Load InoApp in Chromium and verify OwnerTrusted · Squad pill and seated @handle chips.
            await page.GotoAsync(host.ShellFor("apps/ino/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
            await page.Locator("flt-semantics-host").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);

            await WaitForSemanticsTextContainsAsync(page, "OwnerTrusted · Squad", ct);
            await WaitForSemanticsTextContainsAsync(page, "@ino · Lead", ct);
            await WaitForSemanticsTextContainsAsync(page, specialistHandle, ct);

            // 4. Open the Plan / Board right-hand SplitPane drawer and verify all 5 canonical KanbanNeuron columns + CheckBoxNeuron checklist.
            await page.ClickButtonAsync(InoSquads.OpenPlanButtonKey, ct);
            foreach (var column in new[] { "Planned", "In Progress", "Needs You", "Waiting", "Done" })
            {
                await WaitForSemanticsTextContainsAsync(page, column, ct);
            }

            await WaitForSemanticsTextContainsAsync(page, "Squad Checklist", ct);

            // 5. Add a squad task, move it to In Progress on KanbanNeuron, and toggle it Done via CheckBoxNeuron — verifying live sync.
            var squadTaskTitle = $"Verify Kanban Sync {suffix}";
            using (var kernel = host.CreateKernelClient())
            {
                using var addTask = await kernel.PostAsJsonAsync(
                    $"/brains/{host.Scope}/act",
                    new
                    {
                        neuron = $"{host.Scope}/apps/ino",
                        target = ".",
                        signal = "ino.squad-add-task",
                        args = new { title = squadTaskTitle, description = "Live browser sync test", tag = "Squad" },
                    },
                    ct);
                await AssertActSucceededAsync(addTask, "ino.squad-add-task", ct);
            }

            await WaitForSemanticsTextContainsAsync(page, squadTaskTitle, ct);
            await WaitForSemanticsTextContainsAsync(page, "0/1 done", ct);

            using (host.StampOwner())
            {
                var tasks = SiloNeurons.Get<ITasks>(host.Services, ITasks.Key(brainId));
                var allTasks = await tasks.List();
                var squadTask = allTasks.First(t => string.Equals(t.Title, squadTaskTitle, StringComparison.Ordinal));
                createdTaskId = squadTask.TaskId;
            }

            using (var kernel = host.CreateKernelClient())
            {
                using var moveTask = await kernel.PostAsJsonAsync(
                    $"/brains/{host.Scope}/act",
                    new
                    {
                        neuron = $"{host.Scope}/apps/ino",
                        target = ".",
                        signal = "ino.squad-move-task",
                        args = new { taskId = createdTaskId, columnId = "in-progress", index = 0 },
                    },
                    ct);
                await AssertActSucceededAsync(moveTask, "ino.squad-move-task", ct);
            }

            await WaitForTaskStatusAsync(brainId, createdTaskId, TaskStatus.InProgress, ct);

            using (var kernel = host.CreateKernelClient())
            {
                using var toggleTask = await kernel.PostAsJsonAsync(
                    $"/brains/{host.Scope}/act",
                    new
                    {
                        neuron = $"{host.Scope}/apps/ino",
                        target = ".",
                        signal = "ino.squad-toggle-task",
                        args = new { taskId = createdTaskId, @checked = true },
                    },
                    ct);
                await AssertActSucceededAsync(toggleTask, "ino.squad-toggle-task", ct);
            }

            await WaitForTaskStatusAsync(brainId, createdTaskId, TaskStatus.Completed, ct);
            await WaitForSemanticsTextContainsAsync(page, "1/1 done", ct);

            // 6. Seed a pending ChoiceCard on this squad task and verify "Chat about this" -> "Return to choice" preserves pending state.
            const string choiceQuestion = "Which rollout ring should the squad target first?";
            using (host.StampOwner())
            {
                var tasks = SiloNeurons.Get<ITasks>(host.Services, ITasks.Key(brainId));
                await tasks.Ask(createdTaskId, new TaskAskPrompt(
                    Question: choiceQuestion,
                    Options: ["Canary Ring", "Full Production"],
                    RecommendedIndex: 0));
            }

            await WaitForSemanticsTextContainsAsync(page, choiceQuestion, ct);

            // Enter "Chat about this" mode via InoApp signal (or Flutter button) and verify Return to choice restores the ChoiceCard intact.
            var chatAboutBtn = page.GetByRole(AriaRole.Button, new() { Name = "Chat about this" }).First;
            if (await chatAboutBtn.CountAsync().WaitAsync(ct) > 0)
            {
                await chatAboutBtn.ClickAsync(new() { Force = true, Timeout = 15_000 }).WaitAsync(ct);
            }
            else
            {
                using var kernel = host.CreateKernelClient();
                using var enterChatAbout = await kernel.PostAsJsonAsync(
                    $"/brains/{host.Scope}/act",
                    new
                    {
                        neuron = $"{host.Scope}/apps/ino",
                        target = ".",
                        signal = "ino.chat-about-choice",
                        args = new { taskId = createdTaskId, active = true },
                    },
                    ct);
                await AssertActSucceededAsync(enterChatAbout, "ino.chat-about-choice", ct);
            }

            await WaitForSemanticsTextContainsAsync(page, "Return to choice", ct);

            var returnBtn = page.GetByRole(AriaRole.Button, new() { Name = "Return to choice" }).First;
            await returnBtn.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await returnBtn.ClickAsync(new() { Force = true, Timeout = 15_000 }).WaitAsync(ct);

            await WaitForSemanticsTextContainsAsync(page, choiceQuestion, ct);
            await WaitForSemanticsTextContainsAsync(page, "Canary Ring", ct);

            using (host.StampOwner())
            {
                var tasks = SiloNeurons.Get<ITasks>(host.Services, ITasks.Key(brainId));
                var stillPending = await tasks.Read(createdTaskId);
                Assert.NotNull(stillPending?.PendingAsk);
                Assert.Equal(choiceQuestion, stillPending!.PendingAsk!.Question);
            }
        }
        catch (Exception ex)
        {
            throw await BuildDiagnosticFailureAsync("Journey 3 (Specialist, Squad Room, Kanban & ChoiceCard)", page, errors, ex, ct);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(createdTaskId))
            {
                try
                {
                    using (host.StampOwner())
                    {
                        var tasks = SiloNeurons.Get<ITasks>(host.Services, ITasks.Key(brainId));
                        await tasks.Transition(createdTaskId, TaskStatus.Completed, "Gauntlet cleanup");
                    }
                }
                catch
                {
                    // Best-effort cleanup so pending ChoiceCard does not block subsequent tests.
                }
            }

            await CloseDrawerBestEffortAsync(ct);
        }

        Assert.Empty(errors);
    }

    [Fact]
    public async Task Journey4_CrossBrainSharedRoom_LiveGuestMiniApp_And_FailClosedPrivacy()
    {
        var ct = TestContext.Current.CancellationToken;
        var hostBrain = new BrainId(host.Scope);
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var guestAccount = $"guest_{suffix}";
        var outsiderAccount = $"outsider_{suffix}";
        var guestBrain = new BrainId(DigitalBrain.Brains.BrainScope.Create(guestAccount, guestAccount).Id);
        var outsiderBrain = new BrainId(DigitalBrain.Brains.BrainScope.Create(outsiderAccount, outsiderAccount).Id);
        host.ProvisionPersonalBrain(guestAccount, guestBrain.Value);
        host.ProvisionPersonalBrain(outsiderAccount, outsiderBrain.Value);

        await EstablishFriendshipAsync(hostBrain, AccountSession.DefaultLogin, guestBrain, guestAccount);

        var roomTitle = $"Shared Launch Room {suffix}";
        var roomShortKey = $"shared-launch-{suffix}";
        var roomKey = $"conversations/{roomShortKey}";
        var roomId = new NeuronId(hostBrain, IConversation.Type, roomKey);
        var checklistKey = "default";

        using (host.StampOwner())
        {
            var directory = SiloNeurons.Get<IThreadDirectory>(host.Services, new NeuronId(hostBrain, Threads.Type, IThreadDirectory.DefaultKey));
            await directory.Create(roomShortKey, roomTitle, ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom);

            var room = SiloNeurons.Get<IConversation>(host.Services, roomId);
            await room.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, roomTitle);
            await room.Invite(new AccountId(guestAccount), guestBrain, Role.Editor);

            // Fail-closed: host-private screens cannot be pinned to a CrossBrainShared room.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => room.Pin("app.settings", "default"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => room.Pin("app.ino", "default"));

            // Pin [Shareable] RoomChecklistApp and seed initial item.
            await room.Pin("room.checklist", checklistKey);
            await room.Act("room.checklist", checklistKey, new Command(".", "room-checklist.add", JsonSerializer.SerializeToElement(new { label = "Confirm launch window" })));
        }

        // Guest (Editor) adds an item via live AppUiEndpoints.Act and is denied host-private screens.
        var proxies = host.Services.GetRequiredService<INeuronProxies>();
        var hostedNeurons = host.Services.GetService<IHostedNeuronTypes>();
        var edge = host.Services.GetService<EdgeAuthority>();

        using (host.StampPerson(guestAccount, guestBrain.Value))
        {
            var guestCaller = host.Services.GetRequiredService<ICallerAmbient>().Current;
            var guestRead = await AppUiEndpoints.ReadNode(
                UIVocabulary.ViewType,
                $"{host.Scope}/apps/room.checklist/view",
                proxies,
                guestBrain.Value,
                ct,
                caller: guestCaller,
                hostedNeurons: hostedNeurons,
                edge: edge);
            Assert.NotNull(guestRead);

            await AppUiEndpoints.Act(
                new AppUiEndpoints.ActRequest(
                    $"{host.Scope}/apps/room.checklist",
                    ".",
                    "room-checklist.add",
                    JsonSerializer.SerializeToElement(new { label = "Guest QA sign-off" })),
                proxies,
                guestBrain.Value,
                ct,
                guestCaller,
                hostedNeurons: hostedNeurons,
                edge: edge);

            // Fail-closed: guest is ALWAYS denied host-private app.settings and app.ino.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                AppUiEndpoints.ReadNode(
                    UIVocabulary.ViewType,
                    $"{host.Scope}/apps/app.settings/view",
                    proxies,
                    guestBrain.Value,
                    ct,
                    caller: guestCaller,
                    hostedNeurons: hostedNeurons,
                    edge: edge));
        }

        // Fail-closed: non-member outsider is denied access to both the shared room and the pinned mini-app.
        using (host.StampPerson(outsiderAccount, outsiderBrain.Value))
        {
            var outsiderCaller = host.Services.GetRequiredService<ICallerAmbient>().Current;
            var outsiderRoom = SiloNeurons.Get<IConversation>(host.Services, roomId);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => outsiderRoom.Read());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                AppUiEndpoints.ReadNode(
                    UIVocabulary.ViewType,
                    $"{host.Scope}/apps/room.checklist/view",
                    proxies,
                    outsiderBrain.Value,
                    ct,
                    caller: outsiderCaller,
                    hostedNeurons: hostedNeurons,
                    edge: edge));
        }

        await using var context = await host.NewBrowserContextAsync(ViewportWidth, ViewportHeight, ct);
        var page = await context.NewPageAsync();
        var errors = TrackBrowserErrors(page);

        try
        {
            // Switch InoApp to the CrossBrainShared room and open the Pinned Apps drawer in Chromium.
            using (var kernel = host.CreateKernelClient())
            {
                using var switchResp = await kernel.PostAsJsonAsync(
                    $"/brains/{host.Scope}/act",
                    new
                    {
                        neuron = $"{host.Scope}/apps/ino",
                        target = ".",
                        signal = "ino.switch-conversation",
                        args = new { conversationId = roomShortKey },
                    },
                    ct);
                await AssertActSucceededAsync(switchResp, "ino.switch-conversation (shared room)", ct);
            }

            await page.GotoAsync(host.ShellFor("apps/ino/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
            await page.Locator("flt-semantics-host").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);

            await WaitForSemanticsTextContainsAsync(page, "CrossBrainShared · E2EE", ct);
            await page.ClickButtonAsync(InoArcSidebar.OpenPinnedAppsKey, ct);

            // Verify live [Shareable] RoomChecklistApp renders inside the Pinned Apps drawer with both host and guest items.
            await WaitForSemanticsTextContainsAsync(page, "Shared Checklist", ct);
            await WaitForSemanticsTextContainsAsync(page, "Confirm launch window", ct);
            await WaitForSemanticsTextContainsAsync(page, "Guest QA sign-off", ct);
            await WaitForSemanticsTextContainsAsync(page, $"by {guestAccount}", ct);
            await WaitForSemanticsTextContainsAsync(page, "0/2 done", ct);

            // Interactive mutation (Toggle + Add) reflected live in Chromium!
            using (host.StampOwner())
            {
                var room = SiloNeurons.Get<IConversation>(host.Services, roomId);
                await room.Act("room.checklist", checklistKey, new Command(".", "room-checklist.toggle", JsonSerializer.SerializeToElement(new { itemId = "i1", @checked = true })));
                await room.Act("room.checklist", checklistKey, new Command(".", "room-checklist.add", JsonSerializer.SerializeToElement(new { label = "Verify telemetry green" })));
            }

            await WaitForSemanticsTextContainsAsync(page, "1/3 done", ct);
            await WaitForSemanticsTextContainsAsync(page, "Verify telemetry green", ct);
        }
        catch (Exception ex)
        {
            throw await BuildDiagnosticFailureAsync("Journey 4 (CrossBrainShared Room & Live Guest Mini-App)", page, errors, ex, ct);
        }
        finally
        {
            await CloseDrawerBestEffortAsync(ct);
        }

        Assert.Empty(errors);
    }

    private async Task CloseDrawerBestEffortAsync(CancellationToken ct)
    {
        try
        {
            using var kernel = host.CreateKernelClient();
            using var _ = await kernel.PostAsJsonAsync(
                $"/brains/{host.Scope}/act",
                new
                {
                    neuron = $"{host.Scope}/apps/ino",
                    target = ".",
                    signal = "ino.close-drawer",
                    args = new { },
                },
                ct);
        }
        catch
        {
            // Best-effort cleanup between journeys.
        }
    }

    private async Task EstablishFriendshipAsync(BrainId hostBrain, string hostAccount, BrainId friendBrain, string friendAccount)
    {
        Code code;
        using (host.StampPerson(hostAccount, hostBrain.Value))
        {
            var hostFriends = SiloNeurons.Get<IFriends>(host.Services, FriendsDefaults.Id(hostBrain.Value));
            code = await hostFriends.Mint();
        }

        using (host.StampPerson(friendAccount, friendBrain.Value))
        {
            var friendFriends = SiloNeurons.Get<IFriends>(host.Services, FriendsDefaults.Id(friendBrain.Value));
            _ = await friendFriends.Redeem(code);
        }
    }

    private async Task WaitForTaskStatusAsync(BrainId brainId, string taskId, TaskStatus expected, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using (host.StampOwner())
            {
                var tasks = SiloNeurons.Get<ITasks>(host.Services, ITasks.Key(brainId));
                var record = await tasks.Read(taskId);
                if (record?.Status == expected)
                {
                    return;
                }
            }

            await Task.Delay(75, ct);
        }

        throw new Xunit.Sdk.XunitException($"Timed out waiting for task '{taskId}' to reach status '{expected}'.");
    }

    private static EngineerRunSummary CreateEngineerRunSummary(
        string flowId,
        string commitSha,
        bool selfReviewPassed,
        string selfReviewSummary,
        string? confirmationPlanId,
        FlowStatus status,
        string? pushedRef = null,
        string? pullRequestUrl = null)
    {
        var buildReport = new EngineerBuildReport(
            Succeeded: true,
            DotnetBuildPassed: true,
            FlutterAnalyzePassed: true,
            DotnetTestPassed: true,
            PassedTests: 42,
            FailedTests: 0,
            WarningCount: 0,
            ErrorCount: 0,
            Diagnostics: ["Roslyn: 0 errors, 0 warnings", "TRX: 42 passed, 0 failed"],
            DurationMs: 1250,
            Summary: "Build and TRX tests green.");

        var liveReport = new EngineerLiveReport(
            Succeeded: true,
            HostHealthy: true,
            McpProbesPassed: true,
            PlaywrightPassed: true,
            Http4xxCount: 0,
            Http5xxCount: 0,
            ErrorSpanCount: 0,
            ErrorLogCount: 0,
            Probes:
            [
                new EngineerLiveProbe("health-probe", "mcp", true, 200, "MCP tool catalog healthy"),
                new EngineerLiveProbe("drawer-smoke", "playwright", true, 200, "Drawer tabs rendered cleanly"),
            ],
            TraceSummaries: ["span/http.get.health 200 OK (12ms)"],
            Summary: "All live verification probes passed.");

        var selfReviewReport = selfReviewPassed
            ? SelfReviewReport.Empty
            : new SelfReviewReport([new ReviewFinding("INT001", ReviewFindingSource.Analyzer, "apps/ino/InoApp.cs", 42, ReviewSeverity.Blocking, "Missing SHA lock check")]);

        var winnerScore = new EngineerCandidateScore(
            BuildPassed: true,
            TestsPassed: true,
            LiveVerifyPassed: true,
            ZeroTelemetryErrors: true,
            ChangedLines: 24,
            TotalScore: 987,
            CommitSha: commitSha,
            UnifiedDiff: "--- a/apps/ino/InoView.cs\n+++ b/apps/ino/InoView.cs\n@@ -800,3 +800,4 @@\n+// SHA-locked Engineer PR drawer verified\n",
            Summary: "Score=987 · Build=PASS, Tests=PASS, Live=PASS");

        var runnerUpScore = new EngineerCandidateScore(
            BuildPassed: true,
            TestsPassed: true,
            LiveVerifyPassed: false,
            ZeroTelemetryErrors: true,
            ChangedLines: 52,
            TotalScore: 612,
            CommitSha: "9999888877776666555544443333222211110000",
            UnifiedDiff: "+runner-up",
            Summary: "Score=612 · Build=PASS, Tests=PASS, Live=FAIL");

        EngineerCandidateResult[] candidates =
        [
            new(
                InstanceIndex: 0,
                Strategy: "surgical-patch",
                StrategyLabel: "Minimal Surgical Patch",
                LeaseId: "lease-0",
                Backend: "LocalGit",
                BranchName: "ino/engineer-gauntlet",
                Status: "verified",
                CompletedTaskIds: ["t-1"],
                ChangedFiles: ["apps/ino/InoView.cs"],
                Rank: 1,
                IsSelected: true,
                Score: winnerScore,
                Build: buildReport,
                Live: liveReport,
                SelfReview: selfReviewReport),
            new(
                InstanceIndex: 1,
                Strategy: "defensive-hardening",
                StrategyLabel: "Defensive Edge-Case Hardening",
                LeaseId: "lease-1",
                Backend: "LocalGit",
                BranchName: "ino/engineer-gauntlet-alt",
                Status: "completed",
                CompletedTaskIds: ["t-1"],
                ChangedFiles: ["apps/ino/InoView.cs"],
                Rank: 2,
                IsSelected: false,
                Score: runnerUpScore,
                SelfReview: SelfReviewReport.Empty),
        ];

        var receipt = new EngineerVerificationReceipt(
            CommitSha: commitSha,
            PullRequestUrl: pullRequestUrl,
            PushedRef: pushedRef,
            BuildVerified: true,
            LiveVerified: true,
            CandidateHostKind: "LocalSlot",
            VerificationSummary: "Roslyn + TRX + MCP + Playwright + OTel verified.",
            SelfReviewPassed: selfReviewPassed,
            SelfReviewSummary: selfReviewSummary);

        return new EngineerRunSummary(
            Ok: true,
            FlowId: flowId,
            RunId: "gauntlet-run",
            RepoSlug: "intochat/intochat",
            Goal: "Master Plan Engineer PR Drawer Verification",
            Backend: "LocalGit",
            ParallelInstances: 2,
            Status: status,
            CurrentStage: status == FlowStatus.Completed ? "done" : "approve",
            CompletedStages: ["plan", "edit", "build", "live-verify", "self-review"],
            ConfirmationPlanId: confirmationPlanId,
            TraceId: "trace-gauntlet",
            WinningCommitSha: commitSha,
            PushedRef: pushedRef,
            PullRequestUrl: pullRequestUrl,
            EngineerAppUri: "os://window/open?app=ino",
            TasksBoardUri: "os://window/open?app=tasks",
            Plan: null,
            Instances: [],
            DiagnosticsAndTraces: ["otel: 0 error spans across 18 trace events"],
            SummaryText: "Candidate #1 won tournament with score 987.",
            SelectedCandidateIndex: 0,
            Candidates: candidates,
            Receipt: receipt,
            SelfReview: selfReviewReport);
    }

    private static async Task AssertActSucceededAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            // Best-effort diagnostic body read.
        }

        Assert.Fail(string.IsNullOrWhiteSpace(body)
            ? $"{action} failed: {(int)response.StatusCode}"
            : $"{action} failed: {(int)response.StatusCode} — {body}");
    }

    private static List<string> TrackBrowserErrors(IPage page)
    {
        var errors = new List<string>();
        page.Response += (_, response) =>
        {
            if (response.Status >= 500)
            {
                errors.Add($"HTTP {response.Status} {new Uri(response.Url).AbsolutePath}");
            }
        };
        page.PageError += (_, error) => errors.Add($"pageerror: {error}");
        page.Console += (_, msg) =>
        {
            if (string.Equals(msg.Type, "error", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"console.error: {msg.Text}");
            }
        };
        return errors;
    }

    private static async Task<string> ReadAllSemanticsTextAsync(IPage page, CancellationToken ct)
        => await page.EvaluateAsync<string>(
            """
            () => {
                const host = document.querySelector('flt-semantics-host');
                if (!host) return '';
                const parts = [];
                if (host.innerText) parts.push(host.innerText);
                for (const el of host.querySelectorAll('[aria-label]')) {
                    const label = el.getAttribute('aria-label');
                    if (label) parts.push(label);
                }
                return parts.join('\n');
            }
            """).WaitAsync(ct);

    private static async Task WaitForSemanticsTextContainsAsync(IPage page, string expectedSubstring, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var text = await ReadAllSemanticsTextAsync(page, ct);
            if (text.Contains(expectedSubstring, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await page.WaitForTimeoutAsync(150).WaitAsync(ct);
        }

        var finalText = await ReadAllSemanticsTextAsync(page, ct);
        Assert.Contains(expectedSubstring, finalText, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Xunit.Sdk.XunitException> BuildDiagnosticFailureAsync(
        string journeyName,
        IPage page,
        List<string> errors,
        Exception ex,
        CancellationToken ct)
    {
        string visible;
        try
        {
            visible = await ReadAllSemanticsTextAsync(page, ct);
        }
        catch
        {
            visible = "<unavailable>";
        }

        string ids;
        try
        {
            ids = string.Join(", ", (await page.ReadSemanticsIdentifiersAsync(ct)).Take(80));
        }
        catch
        {
            ids = "<unavailable>";
        }

        return new Xunit.Sdk.XunitException(
            $"{journeyName} failed: {ex.Message}\nBrowser errors: [{string.Join(", ", errors)}]\nIdentifiers: [{ids}]\nSemantics text:\n{visible[..Math.Min(2500, visible.Length)]}",
            ex);
    }
}
