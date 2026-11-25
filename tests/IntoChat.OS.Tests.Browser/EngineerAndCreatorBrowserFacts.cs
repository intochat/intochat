using System.Globalization;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Apps.Engineer;
using IntoChat.OS.Modules.Microsoft.Playwright;
using IntoChat.OS.Tests.Browser.Scenarios;
using Microsoft.Playwright;

namespace IntoChat.OS.Tests.Browser;

[Collection(nameof(WebShellCollection))]
public sealed class EngineerAndCreatorBrowserFacts(WebShellHost host)
{
    private const int StudioViewportWidth = 1440;
    private const int StudioViewportHeight = 900;

    [Fact]
    public async Task Creator_flame_camera_hud_minimap_shell_collapse_and_geometry_work_live_without_http_errors()
    {
        var ct = TestContext.Current.CancellationToken;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunch.Options(Environment.GetEnvironmentVariable));
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = StudioViewportWidth, Height = StudioViewportHeight } });
        var page = await context.NewPageAsync();

        var httpErrors = new List<string>();
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                httpErrors.Add($"{response.Status} {new Uri(response.Url).AbsolutePath}");
            }
        };

        await page.GotoAsync(host.ShellFor("apps/creator/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
        await page.KeyedNode("creator-shell").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);
        await page.SceneCameraHud().First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);

        try
        {
            // 1. Hard BoundingBoxAsync geometry & zero-bloat assertions at 1440x900
            var canvasBox = await page.MeasureSceneCanvasBoxAsync("brain", ct);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[GEOMETRY] Creator Flame 3D Canvas: {canvasBox.Format(StudioViewportWidth, StudioViewportHeight)}"));
            Assert.True(
                canvasBox.HeightRatio(StudioViewportHeight) >= 0.65f && canvasBox.Height >= 580f,
                string.Create(CultureInfo.InvariantCulture, $"Creator Flame canvas must occupy >= 65% of 900px viewport (>= 580px), measured: {canvasBox.Format(StudioViewportWidth, StudioViewportHeight)}"));
            Assert.True(
                canvasBox.Y <= 110f,
                string.Create(CultureInfo.InvariantCulture, $"Creator top toolbar must stay compact (canvas Y <= 110px), measured: {canvasBox.Format(StudioViewportWidth, StudioViewportHeight)}"));

            // One inspector tab strip child and no ui.* neuron: the state tab is a jsonTree node, the source tab a code block
            foreach (var oldKind in new[] { "ui.command-bar", "ui.tabs", "ui.slider", "ui.property-grid", "ui.code-editor", "ui.table" })
            {
                Assert.Equal(0, await page.SemanticsPrefix($"neuron:{oldKind}:").CountAsync().WaitAsync(ct));
            }
            Assert.Equal(1, await page.SlotNode(CreatorView.InspectorTabsKey).CountAsync().WaitAsync(ct));

            // 2. Corner Viewport Minimap + Follow Live HUD toggle
            var minimapBox = await page.MeasureKeyedBoxAsync("flame-viewport-minimap", minWidth: 80f, minHeight: 40f, ct);
            Assert.True(
                minimapBox.Width >= 80f && minimapBox.Height >= 40f,
                string.Create(CultureInfo.InvariantCulture, $"Expected corner viewport minimap bounds, measured: {minimapBox.Format(StudioViewportWidth, StudioViewportHeight)}"));
            await page.SceneFollowLive().First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await page.ToggleSceneFollowLiveAsync(ct);
            await page.ClickSceneMinimapAsync(0.45f, 0.5f, ct);

            // 3. Flame Camera HUD: normalize to 100%, zoom in (-> 125%), zoom out, drag pan, fit, and reset back to 100%
            await page.ResetSceneCameraAsync(ct);
            var baselineZoom = await WaitForZoomTargetAsync(page, "100%", ct);
            Assert.Equal("100%", baselineZoom);

            await page.ZoomSceneInAsync(ct);
            var zoomedIn = await WaitForZoomChangeAsync(page, "100%", ct);
            Assert.Equal("125%", zoomedIn);

            await page.ZoomSceneOutAsync(ct);
            await page.PanSceneByDragAsync(80, 40, ct);
            await page.FitSceneCameraAsync(ct);
            await page.ResetSceneCameraAsync(ct);
            var resetZoom = await WaitForZoomTargetAsync(page, "100%", ct);
            Assert.Equal("100%", resetZoom);

            // 4. AppShell collapsible right inspector: collapse widens canvas, re-expand restores inspector
            await page.ShellToggleInspector().First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            var widthWithInspector = (await page.MeasureSceneCanvasBoxAsync("brain", ct)).Width;
            await page.ToggleShellInspectorAsync(ct);
            var widthCollapsed = await WaitForCanvasWidthChangeAsync(page, widthWithInspector, expectWider: true, ct);
            Assert.True(
                widthCollapsed > widthWithInspector + 120f,
                string.Create(CultureInfo.InvariantCulture, $"Collapsing inspector must widen 3D canvas (before={widthWithInspector:0.#}px, collapsed={widthCollapsed:0.#}px)."));

            await page.ToggleShellInspectorAsync(ct);
            await page.SlotNode(CreatorView.InspectorTabsKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            var widthRestored = await WaitForCanvasWidthChangeAsync(page, widthCollapsed, expectWider: false, ct);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[GEOMETRY] Creator Inspector Collapse/Expand Widths: open={widthWithInspector:0.#}px -> collapsed={widthCollapsed:0.#}px -> restored={widthRestored:0.#}px"));
            Assert.True(
                widthRestored < widthCollapsed - 100f,
                string.Create(CultureInfo.InvariantCulture, $"Re-expanding inspector must restore split width (collapsed={widthCollapsed:0.#}px, restored={widthRestored:0.#}px)."));

            // 5. Creator Time-Travel controls + keyboard stepping + Inspector tabs (State, Source, Signals, State Graph, Scenarios)
            await page.KeyedNode("replay-cursor-readout").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await page.SlotNode(CreatorView.ReplaySliderKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);

            await page.StepTimeTravelBackwardAsync(ct);
            await page.StepTimeTravelForwardAsync(ct);
            await page.JumpTimeTravelFirstAsync(ct);
            await page.JumpTimeTravelLastAsync(ct);
            await page.ToggleTimeTravelModeAsync(ct);

            await SelectInspectorTabAndWaitAsync(page, "Source", page.GetByText("No source files kept for the current selection.").First, ct);
            await page.WaitForTimeoutAsync(220).WaitAsync(ct);

            await SelectInspectorTabAndWaitAsync(page, "Signals", page.SlotNode(CreatorView.InspectorSignalsKey).First, ct);
            await page.WaitForTimeoutAsync(220).WaitAsync(ct);

            await SelectInspectorTabAndWaitAsync(page, "State Graph", page.SceneCameraHud().First, ct);
            await page.WaitForTimeoutAsync(220).WaitAsync(ct);

            await SelectInspectorTabAndWaitAsync(page, "Scenarios", page.KeyedNode("run-scenario").First, ct);
        }
        catch (Exception ex)
        {
            var visible = await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.innerText ?? '<no-semantics>'");
            var ids = await page.ReadSemanticsIdentifiersAsync(ct);
            throw new Xunit.Sdk.XunitException(
                $"Creator & Flame E2E failed: {ex.Message}\nHTTP errors: [{string.Join(", ", httpErrors)}]\nIdentifiers: [{string.Join(", ", ids.Take(80))}]\nVisible text:\n{visible[..Math.Min(1500, visible.Length)]}",
                ex);
        }

        Assert.Empty(httpErrors);
    }

    [Fact]
    public async Task Engineer_unified_3d_studio_switches_flow_and_full_height_github_diff_with_candidate_promotion()
    {
        var ct = TestContext.Current.CancellationToken;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunch.Options(Environment.GetEnvironmentVariable));
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = StudioViewportWidth, Height = StudioViewportHeight } });
        var page = await context.NewPageAsync();

        var httpErrors = new List<string>();
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                httpErrors.Add($"{response.Status} {new Uri(response.Url).AbsolutePath}");
            }
        };

        await page.GotoAsync(host.ShellFor("apps/engineer/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
        await page.KeyedNode("engineer-shell").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);

        try
        {
            // 1. Compact Studio header with no ui.* neuron at all + InoNative presence & 3D Flow stage
            Assert.Equal(0, await page.SemanticsPrefix("neuron:ui.").CountAsync().WaitAsync(ct));

            // The tab strip, the brainstorm thread, the worktree tree and the table are control.* children of Engineer.
            await page.SlotNode(EngineerApp.TabsKey).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }).WaitAsync(ct);
            await page.SlotNode(EngineerApp.BrainstormKey).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }).WaitAsync(ct);
            await page.SlotNode(EngineerApp.WorktreesKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await page.SlotNode(EngineerApp.TableKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);

            await WaitForSemanticsTextContainsAsync(page, "Ino Native", ct);
            await WaitForSemanticsTextContainsAsync(page, "Ratified Plan & Ino Slices", ct);

            var flowCanvasBox = await page.MeasureSceneCanvasBoxAsync("brain", ct);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[GEOMETRY] Engineer 3D Flow Canvas: {flowCanvasBox.Format(StudioViewportWidth, StudioViewportHeight)}"));
            Assert.True(
                flowCanvasBox.HeightRatio(StudioViewportHeight) >= 0.65f && flowCanvasBox.Y <= 110f,
                string.Create(CultureInfo.InvariantCulture, $"Engineer 3D Flow stage must be full-bleed (>= 65%h, Y <= 110px), measured: {flowCanvasBox.Format(StudioViewportWidth, StudioViewportHeight)}"));

            var chatText = await WaitForSlotTextContainsAsync(page, EngineerApp.BrainstormKey, "Architect", ct);
            Assert.Contains("Architect", chatText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Critic", chatText, StringComparison.OrdinalIgnoreCase);
            await SubmitBrainstormAndWaitAsync(page, "Verify cloud sandbox lease isolation", ct);
            await WaitForSemanticsTextContainsAsync(page, "Verify cloud sandbox lease isolation", ct);

            // 2. Ratify Plan -> transitions to GitHub Desktop Changes & Diff stage: the changed files beside the candidate's code block
            await page.ClickButtonAsync("engineer:approve-plan", ct);
            await page.KeyedNode("engineer:diff-file:0").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await WaitForSemanticsTextContainsAsync(page, "EngineerFlow.cs", ct);

            // Switch smoothly back to 3D Flow & Plan and back to Verification / PR Review
            await SelectEngineerTabAsync(page, "3D Flow & Plan", ct);
            var returnedFlowBox = await page.MeasureSceneCanvasBoxAsync("brain", ct);
            Assert.True(returnedFlowBox.HeightRatio(StudioViewportHeight) >= 0.65f);

            await page.ClickButtonAsync("engineer:run-verify", ct);
            var verifyTable = await WaitForSlotTextContainsAsync(page, EngineerApp.TableKey, "PASS", ct);
            Assert.Contains("PASS", verifyTable, StringComparison.OrdinalIgnoreCase);

            // 3. Switch the tab strip to PR Review -> verify tournament scoreboard, candidate diff switching, tree selection & PR promotion
            await SelectEngineerTabAsync(page, "PR Review", ct);
            await page.KeyedNode("engineer:pr:approve").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);

            // Default tournament winner is Candidate w1 (#0 · Minimal Surgical Patch · commit aaaaaaaa)
            await WaitForSemanticsTextContainsAsync(page, "Winning candidate: w1", ct);
            await WaitForSemanticsTextContainsAsync(page, "Host-Rendered Unified Diff Preview · Candidate w1 (aaaaaaaa)", ct);
            await WaitForSemanticsTextContainsAsync(page, "app:engineer:eng-01:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ct);
            await WaitForSemanticsTextContainsAsync(page, "EngineerFlow.cs", ct);
            var initialScoreboard = await WaitForSlotTextContainsAsync(page, EngineerApp.TableKey, "998 · SELECTED (Rank #1)", ct);
            Assert.Contains("993 · STANDBY (Rank #2)", initialScoreboard, StringComparison.OrdinalIgnoreCase);

            // Promote Candidate w2 via rule-guarded w2 button -> switches diff card, scoreboard & SHA-bound gate to bbbbbbbb
            await page.ClickButtonAsync(EngineerApp.CandidateSelectKey(1), ct);
            await WaitForSemanticsTextContainsAsync(page, "Promoted candidate w2", ct);
            await WaitForSemanticsTextContainsAsync(page, "Winning candidate: w2", ct);
            await WaitForSemanticsTextContainsAsync(page, "Host-Rendered Unified Diff Preview · Candidate w2 (bbbbbbbb)", ct);
            await WaitForSemanticsTextContainsAsync(page, "app:engineer:eng-01:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", ct);
            var promotedScoreboard = await WaitForSlotTextContainsAsync(page, EngineerApp.TableKey, "993 · SELECTED (Rank #2)", ct);
            Assert.Contains("998 · STANDBY (Rank #1)", promotedScoreboard, StringComparison.OrdinalIgnoreCase);

            // Switch via the worktree tree's file node back to w1 (WorktreeRoslynAnalyzer.cs), then select w2's Dart file & approve PR for w2
            await SelectWorktreeNodeAsync(page, "WorktreeRoslynAnalyzer.cs", ct);
            await WaitForSemanticsTextContainsAsync(page, "Winning candidate: w1", ct);

            await SelectWorktreeNodeAsync(page, "stepper_widget.dart", ct);
            await WaitForSemanticsTextContainsAsync(page, "Winning candidate: w2", ct);

            await page.ClickButtonAsync("engineer:pr:approve", ct);
            await WaitForSemanticsTextContainsAsync(page, "Pushed candidate w2", ct);
        }
        catch (Exception ex)
        {
            var visible = await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.innerText ?? '<no-semantics>'");
            var ids = await page.ReadSemanticsIdentifiersAsync(ct);
            throw new Xunit.Sdk.XunitException(
                $"Engineer Studio E2E failed: {ex.Message}\nHTTP errors: [{string.Join(", ", httpErrors)}]\nIdentifiers: [{string.Join(", ", ids.Take(80))}]\nVisible text:\n{visible[..Math.Min(1500, visible.Length)]}",
                ex);
        }

        Assert.Empty(httpErrors);
    }

    private static async Task<float> WaitForCanvasWidthChangeAsync(IPage page, float previousWidth, bool expectWider, CancellationToken ct)
    {
        for (var i = 0; i < 40; i++)
        {
            var box = await page.MeasureSceneCanvasBoxAsync("brain", ct);
            if (expectWider && box.Width > previousWidth + 100f)
            {
                return box.Width;
            }
            if (!expectWider && box.Width < previousWidth - 100f)
            {
                return box.Width;
            }
            await page.WaitForTimeoutAsync(100).WaitAsync(ct);
        }
        return (await page.MeasureSceneCanvasBoxAsync("brain", ct)).Width;
    }

    private static async Task<string> WaitForZoomChangeAsync(IPage page, string previousZoom, CancellationToken ct)
    {
        for (var i = 0; i < 20; i++)
        {
            var current = await page.ReadSceneZoomTextAsync(ct);
            if (!string.IsNullOrWhiteSpace(current) && !string.Equals(current, previousZoom, StringComparison.Ordinal))
            {
                return current;
            }
            await page.WaitForTimeoutAsync(100).WaitAsync(ct);
        }
        return await page.ReadSceneZoomTextAsync(ct);
    }

    private static async Task<string> WaitForZoomTargetAsync(IPage page, string expectedZoom, CancellationToken ct)
    {
        for (var i = 0; i < 20; i++)
        {
            var current = await page.ReadSceneZoomTextAsync(ct);
            if (string.Equals(current, expectedZoom, StringComparison.Ordinal))
            {
                return current;
            }
            await page.WaitForTimeoutAsync(100).WaitAsync(ct);
        }
        return await page.ReadSceneZoomTextAsync(ct);
    }

    // The text a child of Engineer (a control.* slot) draws, once it holds `expectedSubstring`.
    private static async Task<string> WaitForSlotTextContainsAsync(IPage page, string key, string expectedSubstring, CancellationToken ct)
    {
        var slot = page.SlotNode(key).First;
        var current = string.Empty;
        for (var i = 0; i < 80; i++)
        {
            current = await slot.InnerTextAsync(new() { Timeout = 15_000 }).WaitAsync(ct);
            if (current.Contains(expectedSubstring, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }
            await page.WaitForTimeoutAsync(150).WaitAsync(ct);
        }
        return current;
    }

    // Types into the brainstorm thread's composer (control.chatThread) and sends it; Engineer starts a brainstorm from the text.
    private static async Task SubmitBrainstormAndWaitAsync(IPage page, string message, CancellationToken ct)
    {
        var thread = page.SlotNode(EngineerApp.BrainstormKey).First;
        await thread.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }).WaitAsync(ct);
        var input = thread.Locator("input:not([disabled]):not([readonly]), textarea:not([disabled]):not([readonly])").Last;
        await input.ClickAsync(new() { Timeout = 15_000, Force = true }).WaitAsync(ct);
        await input.FillAsync(message, new() { Timeout = 15_000 }).WaitAsync(ct);
        var send = thread.GetByRole(AriaRole.Button, new() { Name = "Send", Exact = true }).First;
        if (await send.CountAsync().WaitAsync(ct) > 0)
        {
            await send.ClickAsync(new() { Force = true, Timeout = 5_000 }).WaitAsync(ct);
        }
        else
        {
            await input.PressAsync("Enter", new() { Timeout = 5_000 }).WaitAsync(ct);
        }

        await WaitForSemanticsTextContainsAsync(page, "Brainstorm complete", ct);
    }

    // A tab of Engineer's workspace tab strip child (control.tabStrip), which sends control.select through Engineer.
    private static async Task SelectEngineerTabAsync(IPage page, string label, CancellationToken ct)
    {
        var strip = page.SlotNode(EngineerApp.TabsKey).First;
        await strip.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }).WaitAsync(ct);
        var byRole = strip.GetByRole(AriaRole.Tab, new() { Name = label, Exact = true }).First;
        var target = await byRole.CountAsync().WaitAsync(ct) > 0 ? byRole : strip.GetByText(label, new() { Exact = true }).First;
        await target.ClickAsync(new() { Timeout = 15_000, Force = true }).WaitAsync(ct);
    }

    // A node of Engineer's worktree tree child (control.tree), which sends control.select through Engineer.
    private static async Task SelectWorktreeNodeAsync(IPage page, string label, CancellationToken ct)
    {
        var tree = page.SlotNode(EngineerApp.WorktreesKey).First;
        await tree.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }).WaitAsync(ct);
        await tree.GetByText(label, new() { Exact = false }).First.ClickAsync(new() { Timeout = 15_000, Force = true }).WaitAsync(ct);
    }

    private static async Task SelectInspectorTabAndWaitAsync(IPage page, string tabLabel, ILocator expectedTarget, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await SelectCreatorTabAsync(page, tabLabel, ct);
            try
            {
                await expectedTarget.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = attempt == 2 ? 15_000 : 4_000 }).WaitAsync(ct);
                return;
            }
            catch (TimeoutException) when (attempt < 2)
            {
                await page.WaitForTimeoutAsync(200).WaitAsync(ct);
            }
        }
    }

    // A tab of Creator's inspector tab strip child (control.tabStrip), which sends control.select through Creator.
    private static async Task SelectCreatorTabAsync(IPage page, string label, CancellationToken ct)
    {
        var strip = page.SlotNode(CreatorView.InspectorTabsKey).First;
        await strip.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }).WaitAsync(ct);
        var byRole = strip.GetByRole(AriaRole.Tab, new() { Name = label, Exact = true }).First;
        var target = await byRole.CountAsync().WaitAsync(ct) > 0 ? byRole : strip.GetByText(label, new() { Exact = true }).First;
        await target.ClickAsync(new() { Timeout = 15_000, Force = true }).WaitAsync(ct);
    }

    private static async Task WaitForSemanticsTextContainsAsync(IPage page, string expectedSubstring, CancellationToken ct)
    {
        for (var i = 0; i < 300; i++)
        {
            var visible = await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.innerText ?? ''").WaitAsync(ct);
            if (visible.Contains(expectedSubstring, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            await page.WaitForTimeoutAsync(150).WaitAsync(ct);
        }
        var finalText = await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.innerText ?? ''").WaitAsync(ct);
        Assert.Contains(expectedSubstring, finalText, StringComparison.OrdinalIgnoreCase);
    }
}
